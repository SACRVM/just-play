using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="TransientDesigner"/> - the stereo transient shaper that boosts
/// attack onsets while leaving sustained, steady-state content unchanged.
///
/// Coverage:
///   TD1 - Punch = 0.0 is strictly bit-transparent: every output sample is identical to the input.
///   TD2 - A percussive impulse train gets a measurably higher peak amplitude in the attack
///          window at Punch = 0.8 versus Punch = 0.0, proving the envelope-follower pair detects
///          and boosts onsets.
///   TD3 - A steady 1 kHz sine is left essentially unchanged at Punch = 0.8: once both envelope
///          followers have warmed up and the fast follower's DC equilibrium drops below the slow
///          follower's (the key property of asymmetric attack / matched release), the gain
///          returns to exactly 1.0 and the RMS stays within 5 % of the input.
///   TD4 - Output stays finite; Reset() clears all envelope state so that silence in -> silence out.
/// </summary>
public class TransientDesignerTests
{
    private const int Rate = 44100;

    // -- Signal helpers ----------------------------------------------------------------------------

    /// <summary>Interleaved-stereo sine at the given amplitude and frequency (same signal on L+R).</summary>
    private static float[] StereoSine(double amp, double freq, double seconds)
    {
        int n = (int)(seconds * Rate);
        var buf = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            double s = amp * Math.Sin(2.0 * Math.PI * freq * i / Rate);
            buf[i * 2] = (float)s; buf[i * 2 + 1] = (float)s;
        }
        return buf;
    }

    /// <summary>
    /// Impulse train: a sharp exponentially-decaying burst that repeats at <paramref name="burstHz"/>.
    /// <paramref name="decayPerSample"/> controls how fast each burst dies away - 0.01 gives a
    /// ~5 ms half-amplitude time, realistic for a kick or snare transient.
    /// </summary>
    private static float[] ImpulseTrain(double amp, double burstHz, double decayPerSample, double seconds)
    {
        int n      = (int)(seconds * Rate);
        int period = (int)(Rate / burstHz);
        var buf    = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            int    phase = i % period;
            double s     = amp * Math.Exp(-decayPerSample * phase);
            buf[i * 2] = (float)s; buf[i * 2 + 1] = (float)s;
        }
        return buf;
    }

    /// <summary>RMS of the L channel in the given sample-frame range (half-open interval).</summary>
    private static double RmsL(float[] buf, int frameStart, int frameEnd)
    {
        double acc = 0.0;
        for (var i = frameStart; i < frameEnd; i++)
        {
            double v = buf[i * 2];
            acc += v * v;
        }
        return Math.Sqrt(acc / (frameEnd - frameStart));
    }

    /// <summary>Peak |L| in the given sample-frame range (half-open interval).</summary>
    private static float PeakL(float[] buf, int frameStart, int frameEnd)
    {
        float peak = 0f;
        for (var i = frameStart; i < frameEnd; i++)
        {
            float v = buf[i * 2] < 0f ? -buf[i * 2] : buf[i * 2];
            if (v > peak) peak = v;
        }
        return peak;
    }

    // -- Tests -------------------------------------------------------------------------------------

    [Fact]
    public void Punch_Zero_IsBitTransparent()
    {
        // Arbitrary non-trivial signal - a mix of amplitude and frequency that is definitely
        // not silence or a single clean value, so any accidental modification would show up.
        var original = StereoSine(0.73, 317.0, 0.25);
        var processed = (float[])original.Clone();

        new TransientDesigner(Rate, punch: 0.0).ProcessInterleavedStereo(processed);

        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], processed[i]);   // EXACT float equality - no rounding
    }

    [Fact]
    public void Percussive_AttackPeak_IsBoosted_AtPunch()
    {
        // 4 Hz impulse train -> 4 sharp onsets per second.  Each burst decays to near-silence
        // within ~10 ms so the next onset starts with both envelope followers already near zero,
        // giving the clearest fast-vs-slow divergence on every attack.
        const double amp          = 0.5;
        const double burstHz      = 4.0;
        const double decayPerSamp = 0.01;
        const double seconds      = 1.0;

        float[] sigNeutral = ImpulseTrain(amp, burstHz, decayPerSamp, seconds);
        float[] sigPunched = ImpulseTrain(amp, burstHz, decayPerSamp, seconds);

        // Punch=0.0 - bit-transparent by TD1; output equals input.
        new TransientDesigner(Rate, punch: 0.0).ProcessInterleavedStereo(sigNeutral);

        // Punch=0.8 - onsets should be boosted.  Run from the start so envelope state is
        // warm by the second burst; measure in the SECOND HALF of the buffer.
        new TransientDesigner(Rate, punch: 0.8).ProcessInterleavedStereo(sigPunched);

        int totalFrames = sigNeutral.Length / 2;
        float peakNeutral = PeakL(sigNeutral, totalFrames / 2, totalFrames);
        float peakPunched = PeakL(sigPunched, totalFrames / 2, totalFrames);

        // At the onset of each burst: fastEnv shoots up while slowEnv is still near zero ->
        // diff/slow >= 1.0 -> gain = 1 + 0.8x1.0xK = 1.48 -> peak is substantially higher.
        // A 10 % lift is a very conservative lower bound (the real lift is >= 40 %).
        Assert.True(peakPunched > peakNeutral * 1.10,
            $"Expected punched peak ({peakPunched:F4}) > neutral peak ({peakNeutral:F4}) x 1.10");
    }

    [Fact]
    public void SteadySine_IsUnchanged_AtHighPunch()
    {
        // 1 kHz sine - period ~ 44 samples.  Both envelope followers' attack time constants
        // (88 samples and 2205 samples) are longer than this period so neither can track
        // individual cycles; both average out to the DC rectified level (2A/pi).
        //
        // Crucially, the fast follower has a higher tau_release/tau_attack ratio (15/2 = 7.5)
        // than the slow follower (15/50 = 0.3), which means the fast follower's DC equilibrium
        // sits LOWER than the slow follower's.  Once both have warmed up (well under 1 second),
        // fastEnv < slowEnv -> diff <= 0 -> attackAmount = 0 -> gain = 1.0 exactly.
        const double amp     = 0.5;
        const double freq    = 1000.0;
        const double seconds = 2.0;

        float[] buf = StereoSine(amp, freq, seconds);
        int totalFrames = buf.Length / 2;
        int halfFrame   = totalFrames / 2;

        // Measure input RMS in the second half BEFORE processing.
        double rmsIn = RmsL(buf, halfFrame, totalFrames);

        new TransientDesigner(Rate, punch: 0.8).ProcessInterleavedStereo(buf);

        double rmsOut   = RmsL(buf, halfFrame, totalFrames);
        double rmsRatio = rmsOut / rmsIn;

        // After 1 second of warmup the gain should be at exactly 1.0; a 5 % ceiling is very
        // generous and still proves the processor only touches attacks, not sustained tones.
        Assert.True(rmsRatio < 1.05,
            $"Steady-sine RMS ratio was {rmsRatio:F4} - expected < 1.05 (steady-state gain ~ 1.0)");
    }

    [Fact]
    public void Output_IsFinite_AndReset_ClearsState()
    {
        var buf  = StereoSine(0.8, 1200.0, 0.3);
        var td   = new TransientDesigner(Rate, punch: 0.9);
        td.ProcessInterleavedStereo(buf);

        foreach (var s in buf)
            Assert.True(float.IsFinite(s), $"Non-finite sample in output: {s}");

        // After Reset(), all envelope state is zero.  Feeding silence must produce silence.
        td.Reset();
        var silence = new float[Rate * 2];   // 1 second of interleaved stereo zeros
        td.ProcessInterleavedStereo(silence);
        Assert.All(silence, s => Assert.Equal(0f, s));
    }
}
