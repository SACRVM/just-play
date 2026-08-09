namespace JustPlay.Analysis.Tests;

/// <summary>
/// Deterministic signal generators shared by the analysis tests - a click/kick train at a known
/// BPM, a sine at a known frequency, a chord with an unambiguous key, silence, and the two
/// stereo-to-mono conversions (correct frame average vs the interleaved passthrough that
/// <c>DecodeMono</c> used to hand back).
///
/// <para>
/// These were born inline in <see cref="BeatEnergyProbeTests"/>; they moved here when
/// <see cref="DetectionVersionGoldenTests"/> needed the same signals for a different purpose
/// (that probe reads them back through a WAV file to pin its own RIFF glue, the golden feeds
/// them straight into the analysis pipeline through a fake decoder). One copy, so a fixture the
/// two files both call "a 128 BPM click train" cannot quietly become two different signals.
/// </para>
///
/// <para>
/// Everything here is closed-form and RNG-free on purpose: a golden value is only worth
/// recording if the input that produced it is reproducible to the last bit on any machine,
/// including the macOS port. No <c>Random</c>, no time, no file I/O.
/// </para>
/// </summary>
internal static class SyntheticSignals
{
    /// <summary>A pure sine of <paramref name="frequencyHz"/> at <paramref name="amplitude"/>.</summary>
    public static float[] Sine(double seconds, int rate, double frequencyHz, double amplitude)
    {
        var n = (int)(seconds * rate);
        var x = new float[n];
        for (var i = 0; i < n; i++)
            x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / rate));
        return x;
    }

    /// <summary>All-zero buffer - the "no signal at all" edge of every detector.</summary>
    public static float[] Silence(double seconds, int rate) => new float[(int)(seconds * rate)];

    /// <summary>
    /// A single-sample impulse on every beat at <paramref name="bpm"/> - the sharpest possible
    /// onset, used where the question is sample POSITIONING (does a reader/writer drift?) rather
    /// than spectral content.
    /// </summary>
    public static float[] ImpulseTrain(double seconds, int rate, double bpm, double amplitude)
    {
        var n = (int)(seconds * rate);
        var x = new float[n];
        var samplesPerBeat = rate * 60.0 / bpm;
        for (var b = 0; ; b++)
        {
            var at = (int)Math.Round(b * samplesPerBeat);
            if (at >= n) break;
            x[at] = (float)amplitude;
        }
        return x;
    }

    /// <summary>
    /// A four-on-the-floor kick train: an exponentially decaying sine burst of
    /// <paramref name="toneHz"/> on every beat at <paramref name="bpm"/>. Unlike
    /// <see cref="ImpulseTrain"/> this carries real low-band energy, which is what the rhythm and
    /// vibe analyzers actually measure (they work off a low-band onset envelope, and a bare
    /// impulse has its energy spread flat across the whole spectrum).
    /// </summary>
    public static float[] KickTrain(
        double seconds, int rate, double bpm, double toneHz, double decaySeconds, double amplitude)
    {
        var n = (int)(seconds * rate);
        var x = new float[n];
        var samplesPerBeat = rate * 60.0 / bpm;
        // Six time constants is 0.25% of the peak - inaudible, and it keeps every burst finite
        // so the buffer never depends on how many earlier bursts are still ringing.
        var burstLen = (int)(decaySeconds * 6 * rate);

        for (var b = 0; ; b++)
        {
            var start = (int)Math.Round(b * samplesPerBeat);
            if (start >= n) break;
            for (var i = 0; i < burstLen && start + i < n; i++)
            {
                var t = i / (double)rate;
                x[start + i] += (float)(amplitude * Math.Exp(-t / decaySeconds) * Math.Sin(2 * Math.PI * toneHz * t));
            }
        }
        return x;
    }

    /// <summary>
    /// A sustained chord: every note in <paramref name="notes"/> plus <paramref name="harmonics"/>
    /// overtones at 1/k of its amplitude. Two details are load-bearing for key detection: the
    /// overtones (a stack of pure sines gives an HPCP chromagram almost nothing to do harmonic
    /// summation on, and real instruments are not pure sines either), and the per-note amplitude,
    /// which is how the tonic gets weighted above the other chord tones - a bare triad is the same
    /// pitch-class set as its relative major, so an unweighted one is ambiguous by construction.
    /// </summary>
    public static float[] Chord(double seconds, int rate, (double Hz, double Amplitude)[] notes, int harmonics)
    {
        var n = (int)(seconds * rate);
        var x = new float[n];
        foreach (var (f0, amplitude) in notes)
            for (var k = 1; k <= harmonics; k++)
            {
                var f = f0 * k;
                if (f >= rate / 2.0) break;   // never synthesise above Nyquist - that would alias
                var a = amplitude / k;
                for (var i = 0; i < n; i++)
                    x[i] += (float)(a * Math.Sin(2 * Math.PI * f * i / rate));
            }
        return x;
    }

    /// <summary>Scales <paramref name="x"/> so its largest absolute sample is exactly
    /// <paramref name="targetPeak"/>. Returns the input unchanged if it is all zero.</summary>
    public static float[] NormalizePeak(float[] x, double targetPeak)
    {
        var p = Peak(x);
        if (p <= 0f) return x;
        var g = (float)(targetPeak / p);
        var y = new float[x.Length];
        for (var i = 0; i < x.Length; i++) y[i] = x[i] * g;
        return y;
    }

    /// <summary>Builds an L,R,L,R interleaved stereo buffer from two equal-length channels.</summary>
    public static float[] InterleaveStereo(float[] left, float[] right)
    {
        if (left.Length != right.Length)
            throw new ArgumentException("channels must be the same length", nameof(right));
        var x = new float[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            x[i * 2] = left[i];
            x[i * 2 + 1] = right[i];
        }
        return x;
    }

    /// <summary>
    /// The correct stereo-to-mono downmix: average the channels of each FRAME, so N frames in
    /// give N samples out. This is what <c>BassAudioDecoder.DecodeMono</c> does since c687d46;
    /// handing the interleaved buffer back unchanged instead is the bug that commit fixed.
    /// </summary>
    public static float[] FrameAverageStereo(float[] interleaved, int channels = 2)
    {
        var frames = interleaved.Length / channels;
        var y = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            y[i] = sum / channels;
        }
        return y;
    }

    /// <summary>Largest absolute sample magnitude (not the largest signed value).</summary>
    public static float Peak(float[] x)
    {
        var p = 0f;
        foreach (var s in x) { var a = Math.Abs(s); if (a > p) p = a; }
        return p;
    }
}
