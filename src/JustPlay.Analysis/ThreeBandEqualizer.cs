namespace JustPlay.Analysis;

/// <summary>
/// 3-band DJ-style tone EQ for the output bus: Low / Mid / High boost &amp; cut.
///
/// <para><b>Topology — series RBJ filters (low shelf → mid peaking bell → high shelf).</b> Coefficients
/// per the Audio EQ Cookbook (streaming-broadcast.md §6.2). This is the "EQ" mode of a DJ mixer (vs. the
/// "isolator" mode), and it's the RIGHT choice for natural boost/cut:</para>
/// <list type="bullet">
///   <item>At 0 dB each biquad is a mathematical IDENTITY (b == a), so an untouched band is bit-perfect.
///     Boosting LOW therefore leaves the mids/highs <em>completely</em> alone — no coloration.</item>
///   <item>Each filter only affects its own region, so changing one band can't dull another.</item>
/// </list>
///
/// <para><b>Why NOT the Linkwitz-Riley 3-way isolator (the previous version):</b> a 3-way LR crossover
/// does not reconstruct perfectly flat — its phase summation colours the mids/highs, so the moment ANY
/// band leaves unity the whole sound goes slightly dull. Measured/heard 2026-06-15 (Chloe: "raise LOW
/// and it kills the highs, dumpf"). A hardware EQ reconstructs flat; series shelves match that. The
/// trade is that a band "kill" is now a deep CUT (−24 dB), not −∞ — but the isolator's kill wasn't
/// clean either, and natural boost/cut matters far more here. (Full history in §6.5.)</para>
///
/// <para><b>Gains are LINEAR</b> (matching the UI slider): <c>1.0</c> = unity/flat (engine bypasses the
/// DSP entirely → truly transparent), <c>0.0</c> = full cut (mapped to a −24 dB shelf/bell, a deep DJ
/// "kill"), <c>2.0</c> = +6 dB. Corners 200 Hz / 1 kHz / 4 kHz. Stereo: independent state per channel.</para>
///
/// <para>Platform-agnostic, reflection-free, allocation-free on the audio path, trim/AOT-safe.</para>
/// </summary>
public sealed class ThreeBandEqualizer
{
    public int    SampleRate { get; }
    public double LowGain    { get; }
    public double MidGain    { get; }
    public double HighGain   { get; }

    /// <summary>Deepest cut a band can reach (linear 0 maps here). Bounded so the shelf transition
    /// stays tight enough not to bleed into the neighbouring band — a −∞ shelf has too wide a skirt.</summary>
    private const double MinGainDb = -24.0;

    private Biquad _lowL, _midL, _highL;
    private Biquad _lowR, _midR, _highR;

    public ThreeBandEqualizer(
        int    sampleRate  = 44100,
        double lowGain     = 1.0,
        double midGain     = 1.0,
        double highGain    = 1.0,
        double lowFreqHz   = 200.0,
        double midFreqHz   = 1000.0,
        double highFreqHz  = 4000.0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        SampleRate = sampleRate;
        LowGain    = lowGain;
        MidGain    = midGain;
        HighGain   = highGain;

        var low  = Biquad.LowShelf(sampleRate, lowFreqHz,  LinearToDb(lowGain));
        var mid  = Biquad.Peaking(sampleRate, midFreqHz,  LinearToDb(midGain), q: 0.9);
        var high = Biquad.HighShelf(sampleRate, highFreqHz, LinearToDb(highGain));

        _lowL = low; _midL = mid; _highL = high;
        _lowR = low; _midR = mid; _highR = high;   // same coeffs, independent state
    }

    /// <summary>Linear gain → dB, floored at <see cref="MinGainDb"/> so a kill is a deep-but-clean cut.</summary>
    private static double LinearToDb(double linear)
    {
        if (linear <= 0.0) return MinGainDb;
        double db = 20.0 * Math.Log10(linear);
        return db < MinGainDb ? MinGainDb : db;
    }

    /// <summary>Clear all filter state.</summary>
    public void Reset()
    {
        _lowL.Reset(); _midL.Reset(); _highL.Reset();
        _lowR.Reset(); _midR.Reset(); _highR.Reset();
    }

    /// <summary>Process one block of interleaved-stereo float samples (L,R,L,R,…) in place.</summary>
    public void ProcessInterleavedStereo(Span<float> buf)
    {
        int frames = buf.Length / 2;
        for (var i = 0; i < frames; i++)
        {
            int idx = i * 2;
            buf[idx]     = _highL.Process(_midL.Process(_lowL.Process(buf[idx])));
            buf[idx + 1] = _highR.Process(_midR.Process(_lowR.Process(buf[idx + 1])));
        }
    }

    /// <summary>RBJ biquad (Direct Form I) with shelf / peaking factories (Audio EQ Cookbook §6.2).
    /// At 0 dB every type collapses to the identity, so an unmodified band is bit-transparent.</summary>
    private struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public static Biquad LowShelf(int fs, double f0, double dbGain)  => Shelf(fs, f0, dbGain, low: true);
        public static Biquad HighShelf(int fs, double f0, double dbGain) => Shelf(fs, f0, dbGain, low: false);

        private static Biquad Shelf(int fs, double f0, double dbGain, bool low)
        {
            double a  = Math.Pow(10.0, dbGain / 40.0);
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / 2.0 * Math.Sqrt(2.0);          // shelf slope S = 1
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
            double ap1 = a + 1, am1 = a - 1;

            double b0, b1, b2, a0, a1, a2;
            if (low)
            {
                b0 =      a * (ap1 - am1 * cos + twoSqrtAAlpha);
                b1 =  2 * a * (am1 - ap1 * cos);
                b2 =      a * (ap1 - am1 * cos - twoSqrtAAlpha);
                a0 =          ap1 + am1 * cos + twoSqrtAAlpha;
                a1 =     -2 * (am1 + ap1 * cos);
                a2 =          ap1 + am1 * cos - twoSqrtAAlpha;
            }
            else
            {
                b0 =      a * (ap1 + am1 * cos + twoSqrtAAlpha);
                b1 = -2 * a * (am1 + ap1 * cos);
                b2 =      a * (ap1 + am1 * cos - twoSqrtAAlpha);
                a0 =          ap1 - am1 * cos + twoSqrtAAlpha;
                a1 =      2 * (am1 - ap1 * cos);
                a2 =          ap1 - am1 * cos - twoSqrtAAlpha;
            }
            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        public static Biquad Peaking(int fs, double f0, double dbGain, double q)
        {
            double a  = Math.Pow(10.0, dbGain / 40.0);
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2.0 * q);

            double b0 = 1 + alpha * a;
            double b1 = -2 * cos;
            double b2 = 1 - alpha * a;
            double a0 = 1 + alpha / a;
            double a1 = -2 * cos;
            double a2 = 1 - alpha / a;
            return Normalize(b0, b1, b2, a0, a1, a2);
        }

        private static Biquad Normalize(double b0, double b1, double b2, double a0, double a1, double a2)
            => new()
            {
                _b0 = (float)(b0 / a0), _b1 = (float)(b1 / a0), _b2 = (float)(b2 / a0),
                _a1 = (float)(a1 / a0), _a2 = (float)(a2 / a0),
            };

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset() { _x1 = _x2 = _y1 = _y2 = 0f; }
    }
}
