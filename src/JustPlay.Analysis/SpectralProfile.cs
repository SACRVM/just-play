namespace JustPlay.Analysis;

/// <summary>
/// Long-term-average spectrum (LTAS) via Welch's method: Hann-windowed 8192-point FFT frames
/// at 50 % overlap, power accumulated per bin across all frames and averaged temporally, then
/// aggregated into ~1/6-octave log-spaced display bands from 20 Hz to 20 kHz by summing bin
/// power within each band.
///
/// <para><b>Why sum (not average) within each band:</b> summing the time-averaged bin powers over
/// the bins that fall in a 1/6-octave band reproduces the standard interpretation used by RTA meters
/// and mastering tools — each band represents the total power in that frequency slice.  Under this
/// convention white noise (flat PSD) rises at approximately +3 dB/octave in a log-spaced band display
/// (wider bands at higher frequencies capture more bins ∝ f, so more power), while pink noise (PSD ∝
/// 1/f) remains flat per band because the 1/f decay exactly cancels the growing bin count.</para>
///
/// <para>Used by the <c>spectrum</c> CLI command to compare the tonal balance of a track before
/// and after the DSP bus chain.</para>
///
/// <para>Platform-agnostic, reflection-free, allocation-light (buffers allocated once per
/// <see cref="Compute"/> call), trim/AOT-safe.</para>
/// </summary>
public sealed class SpectralProfile
{
    /// <summary>Number of ~1/6-octave bands from 20 Hz to 20 kHz.</summary>
    public const int BandCount = 60;

    private const int    FrameSize = 8192;
    private const int    HopSize   = FrameSize / 2; // 50 % overlap
    private const double DbFloor   = -120.0;

    /// <summary>
    /// Compute the long-term-average spectrum of the supplied mono samples.
    /// Returns one entry per display band: (center frequency in Hz, average power in dB).
    /// Bands above Nyquist, or bands with no FFT bins, are set to <c>−120 dB</c>.
    /// </summary>
    public (double FreqHz, double Db)[] Compute(float[] mono, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(mono);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        // ── Precompute 1/6-octave band edges ─────────────────────────────────
        // Band n: lower = 20 · 2^(n/6),  upper = 20 · 2^((n+1)/6),  centre = 20 · 2^((n+0.5)/6).
        double[] bandLo  = new double[BandCount];
        double[] bandHi  = new double[BandCount];
        double[] bandCtr = new double[BandCount];
        for (int n = 0; n < BandCount; n++)
        {
            bandLo[n]  = 20.0 * Math.Pow(2.0, n          / 6.0);
            bandHi[n]  = 20.0 * Math.Pow(2.0, (n + 1.0)  / 6.0);
            bandCtr[n] = 20.0 * Math.Pow(2.0, (n + 0.5)  / 6.0);
        }

        // ── Hann window (precomputed) ─────────────────────────────────────────
        double[] hann = new double[FrameSize];
        for (int i = 0; i < FrameSize; i++)
            hann[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FrameSize - 1)));

        // ── FFT working buffers ───────────────────────────────────────────────
        float[] re = new float[FrameSize];
        float[] im = new float[FrameSize];

        // ── Accumulate power per bin across frames (Welch's method) ──────────
        int      binCount  = FrameSize / 2 + 1; // DC … Nyquist
        double[] binPower  = new double[binCount];
        int      framesCnt = 0;

        for (int offset = 0; offset + FrameSize <= mono.Length; offset += HopSize)
        {
            for (int i = 0; i < FrameSize; i++)
            {
                re[i] = (float)(mono[offset + i] * hann[i]);
                im[i] = 0f;
            }

            Fft.Forward(re, im);

            for (int k = 0; k < binCount; k++)
                binPower[k] += (double)re[k] * re[k] + (double)im[k] * im[k];

            framesCnt++;
        }

        var result = new (double FreqHz, double Db)[BandCount];

        if (framesCnt == 0)
        {
            for (int n = 0; n < BandCount; n++) result[n] = (bandCtr[n], DbFloor);
            return result;
        }

        // Temporal average: each bin power is now the mean over all frames.
        double inv = 1.0 / framesCnt;
        for (int k = 0; k < binCount; k++)
            binPower[k] *= inv;

        double binHz   = (double)sampleRate / FrameSize;
        double nyquist = sampleRate / 2.0;

        // ── Aggregate bins → log-spaced bands (by SUM) ───────────────────────
        for (int n = 0; n < BandCount; n++)
        {
            if (bandCtr[n] > nyquist)
            {
                result[n] = (bandCtr[n], DbFloor);
                continue;
            }

            double powerSum = 0.0;
            bool   anyBin   = false;

            for (int k = 0; k < binCount; k++)
            {
                double f = k * binHz;
                if (f < bandLo[n]) continue;
                if (f >= bandHi[n]) break;

                powerSum += binPower[k];
                anyBin    = true;
            }

            double db = anyBin && powerSum > 0.0
                ? Math.Max(DbFloor, 10.0 * Math.Log10(powerSum))
                : DbFloor;

            result[n] = (bandCtr[n], db);
        }

        return result;
    }
}
