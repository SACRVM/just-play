using JustPlay.Analysis;

namespace JustPlay.Analysis.Tests;

/// <summary>
/// Unit tests for <see cref="SpectralProfile"/>.
///
/// Coverage:
///   SP1 - A 1 kHz pure sine produces a clear peak in the band containing 1 kHz,
///          well above its immediate neighbours.
///   SP2 - White noise (flat PSD) shows an upward trend of approximately +3 dB/octave
///          in the log-spaced band display (because each higher-frequency band aggregates
///          more FFT bins prop-to f, accumulating more power per band).
///   SP3 - Pink noise (PSD prop-to 1/f) is approximately flat per log-spaced band, within
///          a few dB across 200 Hz - 10 kHz (the region where the IIR approximation
///          is well-behaved).
///   SP4 - All output values are finite (no NaN, no +/-Inf).
///   SP5 - Compute() returns exactly <see cref="SpectralProfile.BandCount"/> entries.
///   SP6 - Calling Compute() on a zero-length signal returns DbFloor (-120 dB) for all bands.
///
/// <para><b>Pink noise generation method (SP3):</b> Paul Kellett's 7-term IIR approximation of
/// a 1/f spectrum [musicdsp.org, Pink Noise]. Seven IIR all-pass stages with geometric pole
/// placements approximate the 1/f roll-off over the audible range. The approximation has
/// roughly +/-3 dB accuracy between 200 Hz and 10 kHz; below 200 Hz and above 10 kHz the
/// approximation diverges, so the flatness test is bounded to that range.</para>
/// </summary>
public class SpectralProfileTests
{
    private const int Rate     = 44100;
    private const double Dur   = 8.0;   // seconds - enough frames for stable Welch average

    private readonly SpectralProfile _prof = new();

    // -- SP1 - 1 kHz tone produces a clear peak -------------------------------

    [Fact]
    public void Tone1kHz_PeakInCorrect1kHzBand()
    {
        float[] mono = Sine(1000.0, 0.5, Dur, Rate);
        var spec     = _prof.Compute(mono, Rate);

        // Find the band whose centre is closest to 1 kHz
        int peakBand = ClosestBand(spec, 1000.0);

        double peakDb = spec[peakBand].Db;

        // Check at least 3 neighbouring bands on each side that are well below the peak
        const double MarginDb = 15.0;
        int lo = Math.Max(0, peakBand - 3);
        int hi = Math.Min(spec.Length - 1, peakBand + 3);
        for (int n = lo; n <= hi; n++)
        {
            if (n == peakBand) continue;
            Assert.True(peakDb - spec[n].Db >= MarginDb,
                $"Band {n} ({spec[n].FreqHz:F0} Hz, {spec[n].Db:F1} dB) is within {MarginDb} dB of " +
                $"peak band {peakBand} ({spec[peakBand].FreqHz:F0} Hz, {peakDb:F1} dB).");
        }
    }

    // -- SP2 - White noise rises ~+3 dB/oct in log-spaced bands --------------

    [Fact]
    public void WhiteNoise_RisesWithFrequencyInLogBands()
    {
        // White noise: equal power per Hz -> more bins per log-spaced band at higher f
        //              -> band SUM grows prop-to f -> approximately +3 dB/oct.
        float[] mono = WhiteNoise(Dur, Rate, seed: 42);
        var spec     = _prof.Compute(mono, Rate);

        // Test the trend over 200 Hz - 8 kHz (well within Nyquist, 5+ octaves)
        double db200  = AverageBandsInRange(spec, 180, 220);
        double db400  = AverageBandsInRange(spec, 360, 440);
        double db800  = AverageBandsInRange(spec, 720, 880);
        double db1600 = AverageBandsInRange(spec, 1440, 1760);
        double db3200 = AverageBandsInRange(spec, 2880, 3520);
        double db6400 = AverageBandsInRange(spec, 5760, 7040);

        // Each octave should be higher than the previous (positive slope)
        Assert.True(db400  > db200  - 1.0, $"200->400 Hz should trend up: {db200:F1} -> {db400:F1} dB");
        Assert.True(db800  > db400  - 1.0, $"400->800 Hz should trend up: {db400:F1} -> {db800:F1} dB");
        Assert.True(db1600 > db800  - 1.0, $"800->1600 Hz should trend up: {db800:F1} -> {db1600:F1} dB");
        Assert.True(db3200 > db1600 - 1.0, $"1600->3200 Hz should trend up: {db1600:F1} -> {db3200:F1} dB");
        Assert.True(db6400 > db3200 - 1.0, $"3200->6400 Hz should trend up: {db3200:F1} -> {db6400:F1} dB");

        // Overall trend: 200 Hz to 6400 Hz = ~5 octaves -> expect ~ +15 dB over 5 oct.
        // Allow generous tolerance (the IIR Welch average may not be perfectly white).
        double totalRise = db6400 - db200;
        Assert.True(totalRise > 5.0,
            $"White noise should rise significantly 200->6400 Hz; got {totalRise:F1} dB total rise.");
    }

    // -- SP3 - Pink noise is approximately flat per log-spaced band ------------

    [Fact]
    public void PinkNoise_ApproximatelyFlatPerLogBand()
    {
        // Pink noise (1/f PSD): equal energy per octave -> flat per log-spaced band.
        // Test the range 200 Hz - 8 kHz where Paul Kellett's IIR is well-behaved (+/-3 dB).
        float[] mono = PinkNoise(Dur, Rate, seed: 99);
        var spec     = _prof.Compute(mono, Rate);

        double db200  = AverageBandsInRange(spec, 180,  220);
        double db400  = AverageBandsInRange(spec, 360,  440);
        double db800  = AverageBandsInRange(spec, 720,  880);
        double db1600 = AverageBandsInRange(spec, 1440, 1760);
        double db3200 = AverageBandsInRange(spec, 2880, 3520);
        double db6400 = AverageBandsInRange(spec, 5760, 7040);

        double[] octaves = [db200, db400, db800, db1600, db3200, db6400];
        double min = octaves.Min(), max = octaves.Max();

        // Within +/-4 dB across 5 octaves (the IIR is ~ +/-3 dB accurate;
        // we add 1 dB margin for finite-signal estimation noise).
        Assert.True(max - min < 8.0,
            $"Pink noise should be flat per log-spaced band (<=8 dB range 200-6400 Hz); " +
            $"got range {min:F1}..{max:F1} dB (spread {max-min:F1} dB).");
    }

    // -- SP4 - All outputs are finite -----------------------------------------

    [Fact]
    public void Output_IsAlwaysFinite()
    {
        float[] mono = WhiteNoise(Dur, Rate, seed: 7);
        var spec     = _prof.Compute(mono, Rate);

        foreach (var (f, db) in spec)
        {
            Assert.True(double.IsFinite(f),  $"FreqHz is not finite: {f}");
            Assert.True(double.IsFinite(db), $"Db is not finite at {f:F0} Hz: {db}");
        }
    }

    // -- SP5 - BandCount entries returned -------------------------------------

    [Fact]
    public void Compute_ReturnsBandCountEntries()
    {
        float[] mono = Sine(440, 0.5, 1.0, Rate);
        var spec     = _prof.Compute(mono, Rate);
        Assert.Equal(SpectralProfile.BandCount, spec.Length);
    }

    // -- SP6 - Zero-length signal -> DbFloor -----------------------------------

    [Fact]
    public void EmptySignal_ReturnsDbFloorForAllBands()
    {
        var spec = _prof.Compute([], Rate);
        Assert.Equal(SpectralProfile.BandCount, spec.Length);
        foreach (var (_, db) in spec)
            Assert.True(db <= -100.0, $"Expected DbFloor but got {db:F1} dB");
    }

    // -- Signal generators -----------------------------------------------------

    private static float[] Sine(double freqHz, double amp, double seconds, int fs)
    {
        int n   = (int)(seconds * fs);
        var buf = new float[n];
        for (int i = 0; i < n; i++)
            buf[i] = (float)(amp * Math.Sin(2.0 * Math.PI * freqHz * i / fs));
        return buf;
    }

    private static float[] WhiteNoise(double seconds, int fs, int seed)
    {
        var rng = new Random(seed);
        int n   = (int)(seconds * fs);
        var buf = new float[n];
        for (int i = 0; i < n; i++)
            buf[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return buf;
    }

    /// <summary>
    /// Paul Kellett 7-term IIR approximation of pink (1/f) noise [musicdsp.org].
    /// Accurate to roughly +/-3 dB in the range 200 Hz - 10 kHz at 44 100 Hz.
    /// </summary>
    private static float[] PinkNoise(double seconds, int fs, int seed)
    {
        var rng = new Random(seed);
        int n   = (int)(seconds * fs);
        var buf = new float[n];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < n; i++)
        {
            double white = rng.NextDouble() * 2.0 - 1.0;
            b0 = 0.99886 * b0 + white * 0.0555179;
            b1 = 0.99332 * b1 + white * 0.0750759;
            b2 = 0.96900 * b2 + white * 0.1538520;
            b3 = 0.86650 * b3 + white * 0.3104856;
            b4 = 0.55000 * b4 + white * 0.5329522;
            b5 = -0.7616  * b5 - white * 0.0168980;
            double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362;
            b6 = white * 0.115926;
            buf[i] = (float)(pink * 0.11);  // scale to approx 0 dBFS range
        }
        return buf;
    }

    // -- Helpers ---------------------------------------------------------------

    private static int ClosestBand((double FreqHz, double Db)[] spec, double targetHz)
    {
        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < spec.Length; i++)
        {
            double d = Math.Abs(spec[i].FreqHz - targetHz);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static double AverageBandsInRange((double FreqHz, double Db)[] spec, double loHz, double hiHz)
    {
        double sum = 0; int n = 0;
        foreach (var (f, db) in spec)
            if (f >= loHz && f <= hiHz && db > -100.0) { sum += db; n++; }
        return n > 0 ? sum / n : -100.0;
    }
}
