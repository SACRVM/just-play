namespace JustPlay.Analysis;

/// <summary>
/// Canonical onset-strength envelope for all rhythm analysis in this project.
///
/// <para>Short-time spectral flux: <see cref="FrameSize"/>-sample Hann-windowed FFT
/// frames at hop <see cref="HopSize"/> (75 % overlap); each frame contributes the
/// half-wave-rectified positive magnitude difference summed across bins. At the
/// usual analysis rate of 11 025 Hz one envelope frame ≈ 11.6 ms.</para>
///
/// <para>This is the single shared implementation (extracted verbatim from
/// <see cref="TempoOctaveCorrector"/>, which now consumes it; external tooling may
/// build on it too). <see cref="BeatFingerprintExtractor"/> and
/// <see cref="RhythmPatternDetector"/> still carry their own MATCHING private copies
/// (the latter a band-split variant producing low/mid envelopes from one FFT pass) —
/// folding those in is a follow-up; until then any parameter change here must be
/// mirrored there (the same MATCH contract their headers already document).</para>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, no NuGet, reflection-free,
/// trim-/AOT-safe.</para>
/// </summary>
public static class OnsetEnvelope
{
    /// <summary>FFT frame length in samples (power of two).</summary>
    public const int FrameSize = 512;

    /// <summary>Hop between consecutive frames in samples (75 % overlap).</summary>
    public const int HopSize = FrameSize / 4;   // 128

    /// <summary>
    /// Builds the onset-strength envelope. One value per hop; envelope index <c>i</c>
    /// covers the analysis frame starting at sample <c>i · HopSize</c>. The first frame
    /// has no predecessor and contributes 0. Returns null when the audio is shorter
    /// than one frame.
    /// </summary>
    /// <param name="samples">Mono float samples in [-1,1].</param>
    /// <param name="ct">Cancellation token.</param>
    public static double[]? Build(float[] samples, CancellationToken ct = default)
    {
        if (samples is null || samples.Length < FrameSize)
            return null;

        var hann     = BuildHannWindow(FrameSize);
        var re       = new float[FrameSize];
        var im       = new float[FrameSize];
        var halfBins = FrameSize / 2;

        var prevMag  = new double[halfBins];
        var havePrev = false;

        var frameCount = (samples.Length - FrameSize) / HopSize + 1;
        var envelope   = new double[frameCount];
        var envIdx     = 0;

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
        {
            ct.ThrowIfCancellationRequested();

            for (var n = 0; n < FrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            double flux = 0.0;
            for (var k = 0; k < halfBins; k++)
            {
                var mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                if (havePrev)
                {
                    var diff = mag - prevMag[k];
                    if (diff > 0.0) flux += diff;
                }
                prevMag[k] = mag;
            }

            if (envIdx < envelope.Length)
                envelope[envIdx++] = havePrev ? flux : 0.0;

            havePrev = true;
        }

        return envelope;
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}
