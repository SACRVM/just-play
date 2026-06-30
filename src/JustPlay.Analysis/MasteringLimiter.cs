namespace JustPlay.Analysis;

/// <summary>
/// Stereo true-peak limiter / maximizer for the master output bus (BASS DSP-callback grade).
///
/// <para>This is the PRODUCTION limiter wired onto the engine mixer — the mono
/// <see cref="TruePeakLimiter"/> prototype it supersedes stays as a reference / offline meter.
/// The two differences that make this one mastering-grade rather than just a safety clamp:</para>
///
/// <list type="number">
///   <item><b>Real BS.1770 true-peak detection.</b> Inter-sample peaks are measured with the
///     same 4× polyphase FIR the reference implementation (libebur128 / ffmpeg) uses: a 49-tap
///     Hann-windowed sinc, generated — not a guessed coefficient table — exactly per
///     <c>interp_create(taps:49, factor:4)</c>. The prototype's 4× linear interpolation
///     under-reads ISPs by up to ~0.5 dB; this does not, so a −1 dBTP ceiling really is −1 dBTP.
///     [ITU-R BS.1770-4 §true-peak; libebur128 ebur128.c interp_create]</item>
///
///   <item><b>Look-ahead with a sliding-maximum window → ISP-safe output, zero attack distortion.</b>
///     The gain for each output sample is derived from the LOUDEST true-peak in the look-ahead
///     window that lies ahead of it, so the gain is already fully reduced before the peak emerges
///     from the delay line. Because the reduction is computed from the <em>true</em> (oversampled)
///     peak and the signal is linear, scaling the base-rate sample by <c>ceiling / truePeak</c>
///     brings the output's true-peak to the ceiling — no separate oversampled output stage needed
///     for safety. [streaming-broadcast.md §R2.5]</item>
/// </list>
///
/// <para><b>Maximizer ("drive"):</b> a makeup gain applied BEFORE detection and to the stored
/// signal, so louder material is pushed up while the limiter catches the resulting peaks — that
/// is what raises perceived loudness for the "club" feel. 0 dB drive = a transparent safety
/// limiter (peaks only, nothing pushed up).</para>
///
/// <para><b>Gain reduction is stereo-LINKED</b> (one envelope drives both channels) so the stereo
/// image never shifts when one channel peaks harder than the other — running two independent mono
/// limiters would pan the mix toward the quieter side on loud transients.</para>
///
/// <para><b>Anti-loudness-war guarantee:</b> the gain envelope is monotone downward (never &gt; 1.0);
/// the limiter only ever reduces. No upward AGC, no "make the quiet bits loud" expansion — the
/// thing Chloe explicitly vetoed for the VDJ-style cheap maximizer. [streaming-broadcast.md §7.2]</para>
///
/// <para><b>Honest scope vs. a commercial mastering limiter (FabFilter Pro-L2 / Ozone Maximizer):</b>
/// detection and peak-safety here are spec-grade. The release is a two-stage (fast→slow) cascade,
/// which is smoother than a single pole but not yet the fully program-adaptive, multi-mode release
/// those plugins tune by ear over years. Closing that last gap is a listening-test iteration, not a
/// correctness fix — tracked in streaming-broadcast.md §R2 "road to parity".</para>
///
/// <para>Platform-agnostic, reflection-free, allocation-free on the audio path, trim/AOT-safe.
/// No Avalonia, no ManagedBass — it just processes an interleaved-stereo float span in place, so
/// the BASS DSP callback and the unit tests drive it identically.</para>
/// </summary>
public sealed class MasteringLimiter
{
    // ── Configuration ────────────────────────────────────────────────────────
    public int    SampleRate  { get; }
    public double CeilingDbTp { get; }
    public double DriveDb     { get; }

    // ── Derived constants ─────────────────────────────────────────────────────
    private readonly float  _ceilingLinear;   // 10^(ceiling/20)
    private readonly float  _drive;           // 10^(drive/20) makeup gain
    private readonly int    _lookahead;       // audio delay / sliding-max window length (samples)
    private readonly double _fastReleaseCoeff;
    private readonly double _slowReleaseCoeff;

    // ── 4× polyphase true-peak FIR (BS.1770 / libebur128) ──────────────────────
    private const int Factor = 4;   // 4× oversampling — BS.1770 minimum
    private const int Taps   = 49;  // odd → symmetric, 12 non-zero taps/phase
    private readonly float[][] _phase;        // [Factor][delayLen] generated Hann-windowed sinc
    private readonly int _firDelayLen;        // (Taps + Factor - 1) / Factor
    private readonly float[] _firHistL;       // per-channel FIR history rings
    private readonly float[] _firHistR;
    private int _firPos;

    // ── Look-ahead audio delay (stores DRIVEN samples) ─────────────────────────
    private readonly float[] _delayL;
    private readonly float[] _delayR;
    private int _delayPos;

    // ── Sliding-maximum of the windowed true-peak (monotonic deque) ────────────
    private readonly long[]  _dqIdx;          // sample index of each candidate
    private readonly float[] _dqVal;          // true-peak value (decreasing front→back)
    private int  _dqHead, _dqTail;            // [head, tail) ring slots in use
    private long _n;                          // running input-sample counter

    // ── Two-stage gain-reduction envelope (linked, ≤ 1.0) ──────────────────────
    private double _grFast = 1.0;
    private double _grSlow = 1.0;

    // ── Live telemetry for the UI gain-reduction lamp ───────────────────────────
    // Written on the BASS audio thread, drained by the UI poll (ReadTelemetry resets them). Plain
    // fields, no lock: a torn read just yields a one-poll-stale lamp, which is invisible at 60 Hz.
    // GR is stereo-LINKED so depth/duty are bus-wide; the per-channel ceiling hits say WHICH channel
    // is driving the limiter (its own true-peak crossed the ceiling) — that's the honest per-channel bit.
    private double _telMinGain = 1.0;   // smallest applied gain since last read → peak gain reduction
    private long   _telActiveFrames;    // frames where the limiter was actually reducing
    private long   _telTotalFrames;
    private bool   _telHitL, _telHitR;  // a channel's true-peak exceeded the ceiling since last read

    /// <param name="sampleRate">Bus sample rate (the JustPlay mixer is 44100).</param>
    /// <param name="ceilingDbTp">True-peak ceiling. −1 dBTP is the broadcast-safe default.</param>
    /// <param name="driveDb">Maximizer makeup gain in dB applied before limiting (0 = safety only).</param>
    /// <param name="lookaheadMs">Look-ahead window in ms (1–5 typical). Adds this much output latency.</param>
    /// <param name="fastReleaseMs">Fast release stage time constant.</param>
    /// <param name="slowReleaseMs">Slow release stage time constant (the smoothing that kills pumping).</param>
    public MasteringLimiter(
        int    sampleRate    = 44100,
        double ceilingDbTp   = -1.0,
        double driveDb       = 0.0,
        double lookaheadMs   = 1.5,
        double fastReleaseMs = 50.0,
        double slowReleaseMs = 250.0)
    {
        if (sampleRate    <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (lookaheadMs   <= 0) throw new ArgumentOutOfRangeException(nameof(lookaheadMs));
        if (fastReleaseMs <= 0) throw new ArgumentOutOfRangeException(nameof(fastReleaseMs));
        if (slowReleaseMs <= 0) throw new ArgumentOutOfRangeException(nameof(slowReleaseMs));

        SampleRate  = sampleRate;
        CeilingDbTp = ceilingDbTp;
        DriveDb     = driveDb;

        _ceilingLinear = (float)Math.Pow(10.0, ceilingDbTp / 20.0);
        _drive         = driveDb == 0.0 ? 1f : (float)Math.Pow(10.0, driveDb / 20.0);

        _lookahead = Math.Max(1, (int)Math.Round(lookaheadMs * sampleRate / 1000.0));

        _fastReleaseCoeff = Math.Exp(-1.0 / (fastReleaseMs * sampleRate / 1000.0));
        _slowReleaseCoeff = Math.Exp(-1.0 / (slowReleaseMs * sampleRate / 1000.0));

        // Build the BS.1770 / libebur128 polyphase FIR. The reference implementation generates the
        // coefficients (it does NOT ship a table): a sinc lowpass at the 4× factor, Hann-windowed,
        // de-interleaved into one subfilter per phase. Reproduced verbatim so true-peak readings
        // match ffmpeg/loudness tooling. [libebur128 ebur128.c interp_create]
        _firDelayLen = (Taps + Factor - 1) / Factor; // = 13
        _phase = new float[Factor][];
        for (var f = 0; f < Factor; f++) _phase[f] = new float[_firDelayLen];
        for (var j = 0; j < Taps; j++)
        {
            double m = j - (Taps - 1) / 2.0;
            double c = 1.0;
            if (m != 0)
            {
                double x = m * Math.PI / Factor;
                c = Math.Sin(x) / x;                       // sinc
            }
            c *= 0.5 * (1 - Math.Cos(2 * Math.PI * j / (Taps - 1))); // Hann window
            _phase[j % Factor][j / Factor] = (float)c;
        }

        _firHistL = new float[_firDelayLen];
        _firHistR = new float[_firDelayLen];

        // Audio delay ring sized to the look-ahead; +1 sentinel so read != write head.
        _delayL = new float[_lookahead + 1];
        _delayR = new float[_lookahead + 1];

        // Deque can hold at most window+1 candidates.
        _dqIdx = new long[_lookahead + 2];
        _dqVal = new float[_lookahead + 2];
    }

    /// <summary>Clears all state (delay lines, FIR history, envelope). Call when (re)enabling.</summary>
    public void Reset()
    {
        Array.Clear(_firHistL); Array.Clear(_firHistR); _firPos = 0;
        Array.Clear(_delayL);   Array.Clear(_delayR);   _delayPos = 0;
        _dqHead = _dqTail = 0;
        _n = 0;
        _grFast = _grSlow = 1.0;
        _telMinGain = 1.0; _telActiveFrames = 0; _telTotalFrames = 0; _telHitL = _telHitR = false;
    }

    /// <summary>Look-ahead latency this limiter adds to the signal, in samples.</summary>
    public int LatencySamples => _lookahead;

    /// <summary>
    /// Current gain reduction in dB, as a NON-NEGATIVE value (0 = not limiting).
    /// Reads directly from the live smoothed gain envelope <c>_grSlow</c> — a plain field read, no lock,
    /// no reset of the telemetry accumulators. Safe to poll at UI frame rate from any thread.
    /// e.g. 2.3 = limiter is currently pulling peaks down by 2.3 dB.
    /// </summary>
    public double LastGainReductionDb =>
        _grSlow < 1.0 ? Math.Max(0.0, -20.0 * Math.Log10(_grSlow)) : 0.0;

    /// <summary>
    /// Snapshot of limiter activity since the previous call, for the UI gain-reduction lamp, and
    /// RESETS the accumulators (call at UI poll rate). <paramref name="gainReductionDb"/> is the
    /// deepest reduction in the interval (≤ 0; 0 = the limiter wasn't reducing); <paramref name="dutyCycle"/>
    /// is the fraction of frames that were being reduced (0..1); the two flags say whether each
    /// channel's true-peak crossed the ceiling (i.e. that channel was driving the limiter).
    /// </summary>
    public void ReadTelemetry(out double gainReductionDb, out double dutyCycle, out bool leftAtCeiling, out bool rightAtCeiling)
    {
        gainReductionDb = _telMinGain >= 1.0 ? 0.0 : 20.0 * Math.Log10(_telMinGain);
        dutyCycle       = _telTotalFrames > 0 ? (double)_telActiveFrames / _telTotalFrames : 0.0;
        leftAtCeiling   = _telHitL;
        rightAtCeiling  = _telHitR;
        _telMinGain = 1.0; _telActiveFrames = 0; _telTotalFrames = 0; _telHitL = _telHitR = false;
    }

    /// <summary>
    /// Process one block of interleaved-stereo float samples (L,R,L,R,…) in place.
    /// State carries across calls so this works as a continuous-stream DSP. An odd-length
    /// span (mono tail) is left as-is on its final sample.
    /// </summary>
    public void ProcessInterleavedStereo(Span<float> buf)
    {
        int frames = buf.Length / 2;
        for (var i = 0; i < frames; i++)
        {
            int idx = i * 2;

            // 1. Apply maximizer drive up front: everything downstream sees the driven signal,
            //    so the limiter both pushes loudness up AND catches the peaks it creates.
            float l = buf[idx]     * _drive;
            float r = buf[idx + 1] * _drive;

            // 2. True-peak of this frame = max |oversampled phase| over both channels (linked).
            //    Keep the per-channel peaks too, so the UI lamp can show which channel is hitting the ceiling.
            float tpL = FirTruePeak(_firHistL, l);
            float tpR = FirTruePeak(_firHistR, r);
            float tp  = Math.Max(tpL, tpR);
            _firPos = (_firPos + 1) % _firDelayLen;
            if (tpL > _ceilingLinear) _telHitL = true;
            if (tpR > _ceilingLinear) _telHitR = true;

            // 3. Push into the sliding-max deque keyed by the running sample index, expire anything
            //    older than the look-ahead window. winMax = loudest upcoming true-peak for the
            //    sample about to leave the delay line.
            PushMax(_n, tp);
            ExpireOlderThan(_n - _lookahead);
            float winMax = _dqVal[_dqHead];

            // 4. Target gain from the TRUE peak → scaling the base-rate sample by this lands the
            //    output true-peak on the ceiling. ≤ 1.0 always (monotone-down, anti-AGC).
            double grTarget = winMax > _ceilingLinear ? _ceilingLinear / winMax : 1.0;

            // 5. Two-stage envelope: instant attack (the window already guarantees the reduction is
            //    in place before the peak arrives → no overshoot, no attack distortion), then a
            //    fast release smoothed by a slow release to suppress pumping.
            if (grTarget < _grFast) _grFast = grTarget;
            else _grFast = _fastReleaseCoeff * _grFast + (1.0 - _fastReleaseCoeff) * grTarget;

            if (_grFast < _grSlow) _grSlow = _grFast;          // track attack instantly
            else _grSlow = _slowReleaseCoeff * _grSlow + (1.0 - _slowReleaseCoeff) * _grFast;

            if (_grSlow > 1.0) _grSlow = 1.0;                  // hard anti-AGC clamp
            float gain = (float)_grSlow;

            // Telemetry: track the deepest reduction + how often we're reducing (duty cycle).
            _telTotalFrames++;
            if (_grSlow < 0.9995) _telActiveFrames++;          // ~0.004 dB threshold = "actually limiting"
            if (_grSlow < _telMinGain) _telMinGain = _grSlow;

            // 6. Read the look-ahead-delayed DRIVEN sample, apply the (now-aligned) gain, write the
            //    current driven sample into the ring.
            int readPos = (_delayPos - _lookahead + _delayL.Length) % _delayL.Length;
            buf[idx]     = gain * _delayL[readPos];
            buf[idx + 1] = gain * _delayR[readPos];
            _delayL[_delayPos] = l;
            _delayR[_delayPos] = r;
            _delayPos = (_delayPos + 1) % _delayL.Length;

            _n++;
        }
    }

    // ── Polyphase FIR: push one sample, return the oversampled true-peak (max |phase|) ──────────
    private float FirTruePeak(float[] hist, float sample)
    {
        hist[_firPos] = sample;
        float peak = 0f;
        for (var f = 0; f < Factor; f++)
        {
            float[] coeff = _phase[f];
            double acc = 0.0;
            for (var t = 0; t < _firDelayLen; t++)
            {
                int h = (_firPos - t + _firDelayLen) % _firDelayLen;
                acc += coeff[t] * hist[h];
            }
            float a = acc < 0 ? (float)-acc : (float)acc;
            if (a > peak) peak = a;
        }
        return peak;
    }

    // ── Monotonic (decreasing) deque for the sliding window maximum ─────────────────────────────
    private void PushMax(long idx, float val)
    {
        // Drop back candidates that this sample dominates — they can never be the window max again.
        while (_dqHead != _dqTail)
        {
            int back = (_dqTail - 1 + _dqVal.Length) % _dqVal.Length;
            if (_dqVal[back] <= val) _dqTail = back;
            else break;
        }
        _dqIdx[_dqTail] = idx;
        _dqVal[_dqTail] = val;
        _dqTail = (_dqTail + 1) % _dqVal.Length;
    }

    private void ExpireOlderThan(long oldestKept)
    {
        while (_dqHead != _dqTail && _dqIdx[_dqHead] < oldestKept)
            _dqHead = (_dqHead + 1) % _dqVal.Length;
    }
}
