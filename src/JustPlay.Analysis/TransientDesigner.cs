namespace JustPlay.Analysis;

/// <summary>
/// Stereo transient designer — enhances the perceived punch and attack of percussive material by
/// boosting the onset of each transient while leaving sustained, steady-state content unchanged.
///
/// <para><b>How it works:</b> a stereo-linked detector signal (max of |L|, |R|) feeds two symmetric
/// IIR envelope followers with very different time constants:</para>
/// <list type="bullet">
///   <item><b>Fast follower</b> (τ = 2 ms): catches the leading edge of a transient within a
///     few milliseconds and drops back to zero within ~20 ms.</item>
///   <item><b>Slow follower</b> (τ = 50 ms): lags behind on attack onsets; represents the
///     "background" signal energy over the past ~250 ms (5 time constants).</item>
/// </list>
/// <para>At the onset of a transient the fast follower races ahead of the slow one; their positive
/// difference, normalized by the slow level, forms the <em>attack amount</em> (0–1). The output
/// gain applied to both channels equally is:</para>
/// <code>gain = 1 + Punch × attackAmount × K</code>
/// <para>where K = 0.6 so that Punch = 1.0 with a pure onset (attackAmount = 1.0) gives
/// approximately +4 dB — a musically noticeable but not extreme lift.</para>
///
/// <para><b>Level-independence:</b> because attackAmount is normalized by the slow envelope level
/// (<c>diff / slowEnv</c>), a transient at −6 dBFS and the same transient at −24 dBFS receive
/// the same proportional boost. The absolute amplitude cancels in the ratio.</para>
///
/// <para><b>Steady-state transparency:</b> both followers use <em>symmetric</em> time constants
/// (equal attack and release τ), so each acts as a linear first-order IIR low-pass on the
/// detector signal.  At any frequency well above 1/τ, a symmetric IIR peak follower converges
/// to the time-average of |signal| = 2A/π regardless of its time constant.  After a warmup
/// period (≈ 5 × τ_slow ≈ 250 ms), both fastEnv and slowEnv converge to the same DC level
/// (2A/π).  The only residual difference is the small ripple the fast follower retains (larger
/// τ → less filtering at 2f), which is typically &lt; 1 % of the signal amplitude at frequencies
/// above 1 kHz — a negligible gain modulation.</para>
///
/// <para><b>Stereo-linked detection:</b> the same gain value is applied to L and R so the stereo
/// image never narrows or shifts when one channel contains a louder attack than the other.</para>
///
/// <para><b>Neutral setting:</b> at <see cref="Punch"/> = 0.0 the audio path is bypassed entirely;
/// output equals input sample-for-sample (bit-transparent) — there is no floating-point
/// multiplication that could alter the signal by even a ULP.</para>
///
/// <para>Platform-agnostic, reflection-free, allocation-free on the audio path, trim/AOT-safe.
/// No Avalonia, no ManagedBass — it just processes an interleaved-stereo float span in place.</para>
/// </summary>
public sealed class TransientDesigner
{
    // ── Configuration ────────────────────────────────────────────────────────
    public int    SampleRate { get; }
    public double Punch      { get; }   // 0.0 = neutral (bit-transparent), up to 1.0 = max punch

    // ── Derived constants ─────────────────────────────────────────────────────
    // K: gain scale — Punch=1.0, attackAmount=1.0 → gain = 1+K ≈ +4 dB.
    // Tune this first if the lift sounds too subtle or too aggressive.
    private const double K = 0.6;

    // Both followers are symmetric (τ_attack = τ_release = τ), making them linear IIR low-pass
    // filters: H(z) = (1−α)/(1−αz⁻¹).  DC gain is exactly 1 regardless of τ, so both converge
    // to the time-average of |signal| = 2A/π for any sustained periodic signal.  After warmup
    // (≈ 5 × τ_slow = 250 ms), fastEnv ≈ slowEnv in steady state → diff → 0 → gain → 1.0.
    // At transient onset (both from near-zero), the fast follower races ahead within a few ms
    // while the slow follower barely moves → large diff → maximum punch.
    private const double FastMs = 2.0;    // 2 ms: catches 1–5 ms kick/snare rise times
    private const double SlowMs = 50.0;   // 50 ms: background-level tracker; 5 τ = 250 ms to full decay

    private readonly double _fastCoeff;   // exp(−1 / (FastAttackMs × fs / 1000))
    private readonly double _slowCoeff;   // exp(−1 / (SlowAttackMs × fs / 1000))

    // ── Envelope state (persistent across ProcessInterleavedStereo calls) ────
    private double _envFast;
    private double _envSlow;

    /// <param name="sampleRate">Audio sample rate in Hz (the JustPlay engine runs at 44 100 Hz).</param>
    /// <param name="punch">
    ///   Punch amount 0.0–1.0.  0.0 = bit-transparent (no processing at all); 1.0 = maximum
    ///   transient boost. Values outside [0, 1] are silently clamped.
    /// </param>
    public TransientDesigner(int sampleRate = 44100, double punch = 0.0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        SampleRate = sampleRate;
        Punch      = Math.Clamp(punch, 0.0, 1.0);

        _fastCoeff = Math.Exp(-1.0 / (FastMs * sampleRate / 1000.0));
        _slowCoeff = Math.Exp(-1.0 / (SlowMs * sampleRate / 1000.0));
    }

    /// <summary>Clears all envelope state. Call when (re)enabling or after a seek.</summary>
    public void Reset()
    {
        _envFast = 0.0;
        _envSlow = 0.0;
    }

    /// <summary>
    /// Process one block of interleaved-stereo float samples (L, R, L, R, …) in place.
    /// State carries across calls so this works correctly as a continuous-stream DSP.
    /// An odd-length span (mono tail) leaves the final lonely sample untouched.
    /// </summary>
    public void ProcessInterleavedStereo(Span<float> buf)
    {
        // At Punch == 0.0 the gain would always be 1.0; skip every multiply so the output
        // equals the input with strict bit-for-bit transparency (no rounding from ×1.0f).
        if (Punch == 0.0) return;

        int    frames = buf.Length / 2;
        double punch  = Punch;   // local copy: avoids repeated property look-ups in the hot loop

        for (var i = 0; i < frames; i++)
        {
            int idx = i * 2;
            float l = buf[idx];
            float r = buf[idx + 1];

            // ── Stereo-linked detector ────────────────────────────────────────────
            // Use the louder channel so both channels always receive IDENTICAL gain →
            // stereo image is preserved even when one side has a harder transient.
            float absL  = l < 0f ? -l : l;
            float absR  = r < 0f ? -r : r;
            float detect = absL > absR ? absL : absR;

            // ── Fast envelope follower (linear IIR — same coeff for attack and release) ──────────
            _envFast = _fastCoeff * _envFast + (1.0 - _fastCoeff) * detect;

            // ── Slow envelope follower (also linear IIR — symmetric 50 ms) ──────────────────────
            _envSlow = _slowCoeff * _envSlow + (1.0 - _slowCoeff) * detect;

            // ── Attack amount (0 … 1, level-independent) ─────────────────────────
            // Positive overshoot of fast above slow, normalized by the slow level.
            // The division by slowEnv removes any dependence on absolute signal amplitude —
            // the same transient shape at −6 dBFS and −24 dBFS gets the same relative boost.
            // Guard against near-zero division at silent passages (epsilon = 1e-7 ≈ −140 dBFS).
            float attackAmount = 0f;
            if (_envSlow > 1e-7)
            {
                double diff = _envFast - _envSlow;
                if (diff > 0.0)
                    attackAmount = (float)Math.Min(1.0, diff / _envSlow);
            }

            // ── Apply gain — same to both channels (stereo-linked) ────────────────
            float gain = (float)(1.0 + punch * attackAmount * K);
            buf[idx]     = l * gain;
            buf[idx + 1] = r * gain;
        }
    }
}
