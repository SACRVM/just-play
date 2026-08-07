namespace JustPlay.Analysis;

/// <summary>
/// Gentle "auto-master" spectral TILT - continuously measures a track's long-term tonal balance
/// and nudges it toward the <see cref="SpectralTarget"/> golden-curve balance without touching dynamics
/// or transient character.
///
/// <para><b>How it works:</b>
/// Two Butterworth measurement filters (LP at <see cref="LowBandHz"/> Hz and HP at
/// <see cref="HighBandHz"/> Hz) feed a slow stereo-linked RMS smoother
/// (tau = <see cref="EnvelopeTauMs"/> ms). The long-term power ratio drives a complementary
/// low-shelf + high-shelf pair that TILTS the output in the opposite direction:</para>
/// <list type="bullet">
///   <item>Duller than the golden curve: lows gently cut, highs gently raised.</item>
///   <item>Brighter/harsher than the golden curve: highs gently cut, lows gently raised.</item>
/// </list>
///
/// <para><b>Design constants (tune by ear - all in one place):</b>
/// <list type="table">
///   <item><term>Low measurement band</term><description>LP at 250 Hz (Butterworth Q=1/sqrt2)</description></item>
///   <item><term>High measurement band</term><description>HP at 3 kHz (Butterworth Q=1/sqrt2)</description></item>
///   <item><term>Target ratio</term><description>DERIVED from the <see cref="SpectralTarget"/> golden
///     curve (see <c>ComputeTargetRatioDb</c>) - NOT 0 dB. A pink-sloped master is naturally bass-heavy
///     through these wideband filters, so a 0 dB (equal-power) setpoint would fight that and brighten
///     already-bright tracks. Tying the setpoint to the same target the spectral diagram uses keeps them
///     consistent.</description></item>
///   <item><term>RMS envelope tau</term><description>300 ms - adapts to the TRACK's tonal balance,
///     not to transients or kick drums.</description></item>
///   <item><term>Gain-smoother tau</term><description>200 ms - prevents zipper noise when biquad
///     coefficients are refreshed every <see cref="UpdatePeriod"/> samples.</description></item>
///   <item><term>Coefficient update period</term><description>256 samples ~ 5.8 ms @ 44100 Hz.
///     The gain smoother ensures the coefficients evolve slowly between updates.</description></item>
///   <item><term>Low shelf corner</term><description>300 Hz (RBJ shelf slope S = 1)</description></item>
///   <item><term>High shelf corner</term><description>3 kHz (RBJ shelf slope S = 1)</description></item>
///   <item><term>Per-shelf gain</term><description>+/-MaxTiltDb/2 - each shelf moves by HALF the
///     total tilt so the combined swing equals MaxTiltDb. Very conservative by design.</description></item>
/// </list>
/// </para>
///
/// <para><b>Safety (user is harshness-sensitive):</b> Strength is clamped to [0, 1]. At Strength = 0
/// the method returns immediately - output is bit-transparent, no processing at all. The total tilt
/// (high shelf - low shelf) is always clamped to +/-<see cref="MaxTiltDb"/> dB, and power floors
/// prevent log(0) or divide-by-zero on silence.</para>
///
/// <para>Platform-agnostic, reflection-free, allocation-free on the audio path, trim/AOT-safe.</para>
/// </summary>
public sealed class AdaptiveTilt
{
    public int    SampleRate { get; }
    public double Strength   { get; }   // clamped to [0, 1]
    public double MaxTiltDb  { get; }

    // -- Design constants - change these to tune by ear -----------------------
    private const double LowBandHz     = 250.0;    // measurement LP corner
    private const double HighBandHz    = 3000.0;   // measurement HP corner
    private const double LowShelfHz    = 300.0;    // correction low-shelf corner
    private const double HighShelfHz   = 3000.0;   // correction high-shelf corner
    private const double EnvelopeTauMs = 300.0;    // RMS smoother time constant
    private const double GainTauMs     = 200.0;    // shelf-gain smoother (anti-zipper)
    private const int    UpdatePeriod  = 256;      // samples between biquad re-computes
    private const double PowerFloor    = 1e-10;    // prevents log(0) on silence

    private readonly float  _strength;
    private readonly float  _maxTiltDb;
    private readonly double _envCoeff;   // exp(-1 / (tau_env * fs))
    private readonly double _gainCoeff;  // exp(-1 / (tau_gain * fs))

    // The high/low band power ratio (dB) a track sitting exactly on the SpectralTarget golden curve
    // produces through the LP/HP measurement filters. AutoTilt aims for THIS ratio - NOT 0 dB.
    // See ComputeTargetRatioDb for why 0 dB was wrong (it fought the natural bass-heaviness of a
    // correctly pink-sloped master and brightened already-bright tracks - verified on Hardstyle 2026-06-16).
    private readonly double _targetRatioDb;

    // -- Measurement filters: LP (low band) and HP (high band) ----------------
    // Shared immutable coefficients; independent per-channel state.
    private readonly MeasCoeffs _measLP;
    private readonly MeasCoeffs _measHP;
    private MeasState _measLPL, _measLPR;
    private MeasState _measHPL, _measHPR;

    // -- Stereo-linked running power (max of |L|,|R| per band, squared, smoothed) --
    private double _lowPower;
    private double _highPower;

    // -- Smoothed tilt level (dB): the TOTAL tilt; high shelf = +half, low = -half --
    private double _smoothTiltDb;

    // -- Correction shelves: same coefficients, independent delay state per channel --
    private ShelfBiquad _lowShelfL,  _lowShelfR;
    private ShelfBiquad _highShelfL, _highShelfR;

    // -- Block counter for low-rate coefficient updates ------------------------
    private int _sampleCount;

    public AdaptiveTilt(int sampleRate = 44100, double strength = 0.0, double maxTiltDb = 3.0)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (maxTiltDb  <= 0) throw new ArgumentOutOfRangeException(nameof(maxTiltDb));

        SampleRate = sampleRate;
        Strength   = Math.Clamp(strength, 0.0, 1.0);
        MaxTiltDb  = maxTiltDb;

        _strength  = (float)Strength;
        _maxTiltDb = (float)maxTiltDb;
        // _envCoeff is applied every SAMPLE inside the loop -> per-sample formula.
        _envCoeff  = Math.Exp(-1.0 / (EnvelopeTauMs * sampleRate / 1000.0));
        // _gainCoeff is applied once per UpdatePeriod SAMPLES in UpdateCoeffs -> block-rate formula.
        // Using the per-sample formula here would stretch the effective tau by 256x.
        _gainCoeff = Math.Exp(-(double)UpdatePeriod / (GainTauMs * sampleRate / 1000.0));

        _measLP = MeasCoeffs.LowPass(sampleRate,  LowBandHz);
        _measHP = MeasCoeffs.HighPass(sampleRate, HighBandHz);

        _targetRatioDb = ComputeTargetRatioDb(LowBandHz, HighBandHz);

        _lowPower  = PowerFloor;
        _highPower = PowerFloor;

        // Initialise shelves to identity (0 dB).
        ApplyShelfGain(0.0);
    }

    /// <summary>Clear all filter state, envelopes, and gain memory.
    /// Call when re-enabling after a bypass or when a new track starts.</summary>
    public void Reset()
    {
        _measLPL = _measLPR = default;
        _measHPL = _measHPR = default;
        _lowPower     = PowerFloor;
        _highPower    = PowerFloor;
        _smoothTiltDb = 0.0;
        _sampleCount  = 0;
        _lowShelfL.Reset(); _lowShelfR.Reset();
        _highShelfL.Reset(); _highShelfR.Reset();
        ApplyShelfGain(0.0);
    }

    /// <summary>Process one block of interleaved-stereo float samples (L,R,L,R,...) in place.
    /// At Strength == 0 returns immediately (bit-transparent, no work done).</summary>
    public void ProcessInterleavedStereo(Span<float> buf)
    {
        if (_strength == 0f) return;   // <- bit-transparent short-circuit

        int frames = buf.Length / 2;
        for (var i = 0; i < frames; i++)
        {
            int idx = i * 2;
            float l = buf[idx];
            float r = buf[idx + 1];

            // -- Measurement: run LP/HP to isolate the two bands --------------
            float bandLowL  = _measLP.Process(ref _measLPL, l);
            float bandLowR  = _measLP.Process(ref _measLPR, r);
            float bandHighL = _measHP.Process(ref _measHPL, l);
            float bandHighR = _measHP.Process(ref _measHPR, r);

            // Stereo-linked: use the louder channel so one quiet side can't hide a big tonal shift.
            float lowSmp  = Math.Max(Math.Abs(bandLowL),  Math.Abs(bandLowR));
            float highSmp = Math.Max(Math.Abs(bandHighL), Math.Abs(bandHighR));

            // RMS smoother: accumulate squared amplitude.
            _lowPower  = _envCoeff * _lowPower  + (1.0 - _envCoeff) * (lowSmp  * lowSmp);
            _highPower = _envCoeff * _highPower + (1.0 - _envCoeff) * (highSmp * highSmp);

            // -- Low-rate coefficient refresh ---------------------------------
            if (++_sampleCount >= UpdatePeriod)
            {
                _sampleCount = 0;
                UpdateCoeffs();
            }

            // -- Apply correction shelves (in series: low shelf -> high shelf) --
            buf[idx]     = _highShelfL.Process(_lowShelfL.Process(l));
            buf[idx + 1] = _highShelfR.Process(_lowShelfR.Process(r));
        }
    }

    // -- Target ratio derived from the golden curve ---------------------------

    /// <summary>
    /// The high/low band power ratio (dB) that a track sitting exactly on the <see cref="SpectralTarget"/>
    /// golden curve produces when measured through the LP (<paramref name="lpHz"/>) and HP
    /// (<paramref name="hpHz"/>) Butterworth filters. This is AutoTilt's setpoint.
    ///
    /// <para>Why not 0 dB: the measurement bands are WIDEBAND (LP captures every octave below its corner,
    /// HP every octave above), and a correctly pink-sloped master integrates to a clearly bass-heavy
    /// wideband ratio. Aiming for 0 dB (equal power) therefore treats normal bass-heaviness as an error
    /// and lifts the highs - which on bass-forward genres (Hardstyle/Hardcore) pushes an already-bright
    /// track even further over the curve. Deriving the setpoint from the same target the rest of the
    /// tooling uses keeps AutoTilt and the spectral diagram consistent by construction.</para>
    /// </summary>
    private static double ComputeTargetRatioDb(double lpHz, double hpHz)
    {
        double lowP = 0.0, highP = 0.0;
        const int n = 400;
        for (var i = 0; i < n; i++)
        {
            // Log-spaced sweep 20 Hz ... 20 kHz (equal weight per log step).
            double f = 20.0 * Math.Pow(20000.0 / 20.0, (i + 0.5) / n);
            double targetLin = Math.Pow(10.0, SpectralTarget.DbAt(f) / 10.0);   // target power
            // 2nd-order Butterworth squared magnitudes: |LP|^2 = 1/(1+(f/fc)^4), |HP|^2 = (f/fc)^4/(1+(f/fc)^4).
            double rl = f / lpHz, rh = f / hpHz;
            double rl4 = rl * rl * rl * rl, rh4 = rh * rh * rh * rh;
            lowP  += targetLin * (1.0 / (1.0 + rl4));
            highP += targetLin * (rh4 / (1.0 + rh4));
        }
        return 10.0 * Math.Log10(Math.Max(highP, 1e-12) / Math.Max(lowP, 1e-12));
    }

    // -- Coefficient update (called every UpdatePeriod samples) ---------------

    private void UpdateCoeffs()
    {
        // Compute long-term band RMS. Power floor prevents log(0) or div/0 on silence.
        double lowRms  = Math.Sqrt(Math.Max(_lowPower,  PowerFloor));
        double highRms = Math.Sqrt(Math.Max(_highPower, PowerFloor));

        // Measured high/low band power ratio in dB (20-log10 of an RMS ratio == 10-log10 of a power ratio).
        double imbalanceDb = 20.0 * Math.Log10(highRms / lowRms);

        // Error = distance from the GOLDEN-CURVE ratio (not 0 dB). If the track is brighter than the
        // golden curve (imbalance > target) the error goes negative -> highs cut / lows raised. If duller
        // -> highs raised. A track already matching the curve produces ~zero error -> no tilt.
        double errorDb  = _targetRatioDb - imbalanceDb;
        double targetDb = Math.Clamp(errorDb * _strength, -_maxTiltDb, _maxTiltDb);

        // Smooth the target to prevent zipper noise when we recompute the biquad below.
        _smoothTiltDb = _gainCoeff * _smoothTiltDb + (1.0 - _gainCoeff) * targetDb;

        ApplyShelfGain(_smoothTiltDb);
    }

    /// <summary>Recompute and push new shelf coefficients.
    /// High shelf = +tiltDb/2; low shelf = -tiltDb/2. They move in OPPOSITE directions
    /// so the total spectral swing equals <paramref name="tiltDb"/>.</summary>
    private void ApplyShelfGain(double tiltDb)
    {
        double halfDb = tiltDb / 2.0;
        var (b0, b1, b2, a1, a2) = ComputeShelf(SampleRate, LowShelfHz,  -halfDb, low: true);
        _lowShelfL.UpdateCoeffs(b0, b1, b2, a1, a2);
        _lowShelfR.UpdateCoeffs(b0, b1, b2, a1, a2);

        (b0, b1, b2, a1, a2) = ComputeShelf(SampleRate, HighShelfHz, +halfDb, low: false);
        _highShelfL.UpdateCoeffs(b0, b1, b2, a1, a2);
        _highShelfR.UpdateCoeffs(b0, b1, b2, a1, a2);
    }

    /// <summary>RBJ shelf coefficients, shelf slope S = 1 (Audio EQ Cookbook).
    /// Returns pre-normalized (b0,b1,b2,a1,a2): a0 already divided out.</summary>
    private static (float b0, float b1, float b2, float a1, float a2) ComputeShelf(
        int fs, double f0, double dbGain, bool low)
    {
        double a   = Math.Pow(10.0, dbGain / 40.0);
        double w0  = 2.0 * Math.PI * f0 / fs;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha          = sin / 2.0 * Math.Sqrt(2.0);   // S = 1
        double twoSqrtAAlpha  = 2.0 * Math.Sqrt(a) * alpha;
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
        return ((float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0),
                (float)(a1 / a0), (float)(a2 / a0));
    }

    // -- Per-channel shelf biquad: updateable coefficients, preserved delay state --

    private struct ShelfBiquad
    {
        private float _b0, _b1, _b2, _a1, _a2;   // coefficients (refreshed periodically)
        private float _x1, _x2, _y1, _y2;         // delay state (preserved across updates)

        /// <summary>Push new coefficients without disturbing the delay state.</summary>
        public void UpdateCoeffs(float b0, float b1, float b2, float a1, float a2)
        { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset() { _x1 = _x2 = _y1 = _y2 = 0f; }
    }

    // -- Butterworth LP/HP measurement filter (Q = 1/sqrt2) ---------------------

    /// <summary>Per-channel Direct-Form-I biquad delay state.</summary>
    private struct MeasState { public float X1, X2, Y1, Y2; }

    /// <summary>Immutable Butterworth biquad coefficients (Audio EQ Cookbook Sec.6.2).</summary>
    private readonly struct MeasCoeffs
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;

        private MeasCoeffs(float b0, float b1, float b2, float a1, float a2)
        { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }

        public static MeasCoeffs LowPass(int fs, double f0)  => Make(fs, f0, highPass: false);
        public static MeasCoeffs HighPass(int fs, double f0) => Make(fs, f0, highPass: true);

        private static MeasCoeffs Make(int fs, double f0, bool highPass)
        {
            const double q  = 0.70710678;
            double w0       = 2.0 * Math.PI * f0 / fs;
            double cos      = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha    = sin / (2.0 * q);
            double a0       = 1.0 + alpha;
            double b0, b1, b2;
            if (highPass) { b0 = (1.0 + cos) / 2.0; b1 = -(1.0 + cos); b2 = (1.0 + cos) / 2.0; }
            else          { b0 = (1.0 - cos) / 2.0; b1 =  (1.0 - cos); b2 = (1.0 - cos) / 2.0; }
            double a1 = -2.0 * cos, a2 = 1.0 - alpha;
            return new MeasCoeffs(
                (float)(b0 / a0), (float)(b1 / a0), (float)(b2 / a0),
                (float)(a1 / a0), (float)(a2 / a0));
        }

        public float Process(ref MeasState s, float x)
        {
            float y = _b0 * x + _b1 * s.X1 + _b2 * s.X2 - _a1 * s.Y1 - _a2 * s.Y2;
            s.X2 = s.X1; s.X1 = x;
            s.Y2 = s.Y1; s.Y1 = y;
            return y;
        }
    }
}
