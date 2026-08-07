namespace JustPlay.Analysis;

/// <summary>
/// (*) ONE live spectrum tap: rolls the most recent audio into a fixed FFT window and turns it into
/// <see cref="SpectralProfile.BandCount"/> log-spaced 1/6-octave band POWERS (20 Hz...20 kHz) - the
/// same banding the offline <see cref="SpectralProfile"/> and the shared <c>SpectrumWindow</c> speak.
///
/// <para><b>Why it lives here.</b> Two players in the suite grew this exact FFT-band code
/// independently and now need the identical measurement for their own spectrum window. Written ONCE
/// (suite rule) so both measure the same thing instead of drifting two hand-copied FFTs apart. The
/// audio-engine DSP wiring stays per-player - the only app-specific part; this is just the math over
/// a float buffer, so it is platform-agnostic, deterministic, reflection-free and belongs in Analysis.</para>
///
/// <para><b>Threading.</b> <see cref="Capture"/> runs on the audio thread; <see cref="ReadBands"/>
/// on the UI thread. A lock guards the one shared snapshot buffer (a short copy under the lock, all
/// FFT work outside it); the FFT scratch is UI-thread-only. One tap = one signal - a dry/wet display
/// owns two instances.</para>
/// </summary>
public sealed class SpectrumTap
{
    /// <summary>FFT window length. 2048 @ 44.1 kHz ~ 46 ms - enough resolution for the low bands,
    /// short enough to follow a moving mix.</summary>
    public const int FftSize = 2048;

    private static readonly float[] Hann = BuildHann(FftSize);

    private readonly float[] _snapshot = new float[FftSize];   // most-recent mono window
    private readonly object _lock = new();                     // guards _snapshot only

    // FFT scratch - UI-thread only (ReadBands), never touched by the audio thread.
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];

    /// <summary>
    /// AUDIO THREAD. Fold the most recent up-to-<see cref="FftSize"/> interleaved-stereo frames into
    /// the mono snapshot, shifting older content left when the block is shorter than the window.
    /// </summary>
    public void Capture(ReadOnlySpan<float> interleavedStereo)
    {
        var frames = interleavedStereo.Length / 2;
        if (frames <= 0) return;

        lock (_lock)
        {
            if (frames >= FftSize)
            {
                var offset = frames - FftSize;
                for (var i = 0; i < FftSize; i++)
                {
                    var s = (offset + i) * 2;
                    _snapshot[i] = (interleavedStereo[s] + interleavedStereo[s + 1]) * 0.5f;
                }
            }
            else
            {
                var keep = FftSize - frames;
                for (var i = 0; i < keep; i++) _snapshot[i] = _snapshot[i + frames];
                for (var i = 0; i < frames; i++)
                {
                    var s = i * 2;
                    _snapshot[keep + i] = (interleavedStereo[s] + interleavedStereo[s + 1]) * 0.5f;
                }
            }
        }
    }

    /// <summary>
    /// UI THREAD. Window the current snapshot, FFT it, and fill <paramref name="bands"/> with the
    /// summed linear POWER in each 1/6-octave band from 20 Hz up. The caller converts to dB, so the
    /// curve compares directly to a golden target. Fills up to
    /// <c>min(bands.Length, <see cref="SpectralProfile.BandCount"/>)</c> bands.
    /// </summary>
    /// <param name="bands">Destination band-power span (ideally <see cref="SpectralProfile.BandCount"/> long).</param>
    /// <param name="sampleRateHz">The tapped stream's sample rate - sets the FFT bin width.</param>
    public void ReadBands(Span<float> bands, double sampleRateHz)
    {
        lock (_lock) Array.Copy(_snapshot, _re, FftSize);   // all FFT work happens outside the lock

        for (var i = 0; i < FftSize; i++) _re[i] *= Hann[i];
        Array.Clear(_im, 0, FftSize);

        Fft.Forward(_re, _im);

        var half = FftSize / 2;
        for (var i = 0; i < half; i++)
            _re[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);   // reuse re as magnitude scratch

        FillBands(_re, half, sampleRateHz / FftSize, bands);
    }

    /// <summary>
    /// RENDER/UI thread. Welch-average the band POWER over a WHOLE buffer - slide 50 %-overlapped Hann
    /// windows across it and average the per-band power. This is the stable spectrum of a rendered loop
    /// (the render analyzer measures the RENDER, not a live tap): a single-window snapshot of a periodic
    /// beat would be biased by wherever it landed (a kick vs a gap). Allocates its own scratch - call off
    /// the audio thread. Same banding as <see cref="ReadBands"/>, so the two are directly comparable, and
    /// the window's per-curve self-anchoring makes the absolute scale irrelevant. The window count is
    /// capped so a long loop stays cheap.
    /// </summary>
    public static void AverageBands(ReadOnlySpan<float> mono, Span<float> bands, double sampleRateHz)
    {
        var fill = Math.Min(bands.Length, SpectralProfile.BandCount);
        bands[..fill].Clear();
        if (fill == 0 || mono.Length < FftSize) return;

        var re = new float[FftSize];
        var im = new float[FftSize];
        var window = new float[fill];
        var acc = new double[fill];
        var half = FftSize / 2;
        var binHz = sampleRateHz / FftSize;

        const int Hop = FftSize / 2;             // 50 % overlap
        const int MaxWindows = 96;               // cap the FFT count on long loops
        var available = (mono.Length - FftSize) / Hop + 1;
        var stride = Math.Max(1, available / MaxWindows);
        var count = 0;

        for (var start = 0; start + FftSize <= mono.Length; start += Hop * stride)
        {
            for (var i = 0; i < FftSize; i++) re[i] = mono[start + i] * Hann[i];
            Array.Clear(im, 0, FftSize);
            Fft.Forward(re, im);
            for (var i = 0; i < half; i++) re[i] = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
            FillBands(re, half, binHz, window);
            for (var b = 0; b < fill; b++) acc[b] += window[b];
            count++;
        }

        if (count == 0) return;
        for (var b = 0; b < fill; b++) bands[b] = (float)(acc[b] / count);
    }

    /// <summary>Sum the linear POWER of the FFT magnitudes into 1/6-octave bands from 20 Hz up - shared
    /// by the live <see cref="ReadBands"/> and the render-average <see cref="AverageBands"/> so both speak
    /// the identical banding. <paramref name="magnitude"/> holds the first <paramref name="half"/> bins.</summary>
    private static void FillBands(float[] magnitude, int half, double binHz, Span<float> bands)
    {
        var fill = Math.Min(bands.Length, SpectralProfile.BandCount);
        bands[..fill].Clear();
        for (var b = 0; b < fill; b++)
        {
            var loHz = 20.0 * Math.Pow(2.0, b / 6.0);
            var hiHz = 20.0 * Math.Pow(2.0, (b + 1) / 6.0);
            var lo = Math.Max(0, (int)(loHz / binHz));
            var hi = Math.Min(half - 1, (int)(hiHz / binHz));
            var power = 0f;
            for (var i = lo; i <= hi; i++) power += magnitude[i] * magnitude[i];
            bands[b] = power;
        }
    }

    /// <summary>Forget the captured audio - draws as silence until the next <see cref="Capture"/>.</summary>
    public void Clear()
    {
        lock (_lock) Array.Clear(_snapshot, 0, FftSize);
    }

    private static float[] BuildHann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }
}
