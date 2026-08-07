namespace JustPlay.Analysis;

/// <summary>
/// Generic reference ("golden") target spectrum curve for tonal balance comparison in the
/// <c>spectrum</c> CLI command.
///
/// <para><b>Rationale - pink-noise / equal-energy-per-octave slope:</b>
/// Pink noise has power spectral density prop-to 1/f, giving equal energy per octave.  In a
/// log-spaced (per-octave or per-1/6-octave) band display, pink noise appears flat, and this
/// is the widely accepted baseline for well-mastered programme material (EBU R128 production
/// guidance, Toole "Sound Reproduction" Sec.11, Theile equal-energy reference).  The practical
/// target below uses this slope:</para>
/// <list type="bullet">
///   <item>Below ~<see cref="FlatBelowHz"/> (120 Hz): flat - equal-energy sub/bass content.</item>
///   <item>Above 120 Hz: <see cref="SlopeDbPerOctave"/> = -3 dB/octave (the equal-energy / pink-noise
///     slope), anchored to 0 dB at 1 kHz.  At 20 kHz this gives roughly -13 dB, consistent with
///     EBU technical guidance for reference monitoring curves.</item>
/// </list>
///
/// <para>Platform-agnostic, reflection-free, allocation-free, trim/AOT-safe.</para>
/// </summary>
public static class SpectralTarget
{
    // -- Frequency zone boundaries (Hz) ---------------------------------------

    /// <summary>Sub-bass zone lower bound (Hz).</summary>
    public const double SubLo     =    20.0;
    /// <summary>Sub-bass zone upper bound (Hz).</summary>
    public const double SubHi     =    60.0;

    /// <summary>Bass zone lower bound (Hz).</summary>
    public const double BassLo    =    60.0;
    /// <summary>Bass zone upper bound (Hz).</summary>
    public const double BassHi    =   250.0;

    /// <summary>Low-mid / mud zone lower bound (Hz).</summary>
    public const double MudLo     =   250.0;
    /// <summary>Low-mid / mud zone upper bound (Hz).</summary>
    public const double MudHi     =   500.0;

    /// <summary>Mid zone lower bound (Hz).</summary>
    public const double MidLo     =   500.0;
    /// <summary>Mid zone upper bound (Hz).</summary>
    public const double MidHi     =  2000.0;

    /// <summary>Presence / fatigue zone lower bound (Hz).</summary>
    public const double FatigueLo =  2000.0;
    /// <summary>Presence / fatigue zone upper bound (Hz).</summary>
    public const double FatigueHi =  5000.0;

    /// <summary>Air zone lower bound (Hz).</summary>
    public const double AirLo     =  8000.0;
    /// <summary>Air zone upper bound (Hz).</summary>
    public const double AirHi     = 16000.0;

    // -- Curve shape parameters ------------------------------------------------

    /// <summary>Frequency below which the reference curve is flat (Hz).
    /// Represents the equal-energy sub/bass plateau of typical mastered material.</summary>
    public const double FlatBelowHz      = 120.0;

    /// <summary>Roll-off rate above <see cref="FlatBelowHz"/>: -3 dB per octave (equal-energy / pink slope).</summary>
    public const double SlopeDbPerOctave = -3.0;

    /// <summary>Anchor frequency: curve is defined as 0 dB here.</summary>
    public const double AnchorHz         = 1000.0;

    /// <summary>Tolerance band half-width (+/-dB) for shading on the spectrum plot.</summary>
    public const double ToleranceDb      = 3.0;

    // Precompute the dB value at the flat/slope junction so DbAt is branch-minimised.
    private static readonly double FlatDb =
        SlopeDbPerOctave * Math.Log2(FlatBelowHz / AnchorHz);  // ~ +9.18 dB

    /// <summary>
    /// Reference level in dB at <paramref name="freqHz"/>.
    /// <list type="bullet">
    ///   <item>f <= 120 Hz: returns a constant value (~+9.2 dB) - the flat sub-bass plateau.</item>
    ///   <item>f &gt; 120 Hz: returns <c>-3 - log_2(f / 1000)</c> - the equal-energy roll-off.</item>
    /// </list>
    /// At 1 kHz = 0 dB (anchor), at 10 kHz ~ -10 dB, at 20 kHz ~ -13 dB.
    /// Returns <see cref="double.NaN"/> for non-positive frequencies.
    /// </summary>
    public static double DbAt(double freqHz)
    {
        if (freqHz <= 0.0) return double.NaN;
        double db = SlopeDbPerOctave * Math.Log2(freqHz / AnchorHz);
        // Cap at the flat-region value: below 120 Hz the uncapped slope would rise unboundedly.
        return db < FlatDb ? db : FlatDb;
    }

    /// <summary>Upper tolerance bound at <paramref name="freqHz"/> (+<see cref="ToleranceDb"/> dB).</summary>
    public static double UpperDb(double freqHz) => DbAt(freqHz) + ToleranceDb;

    /// <summary>Lower tolerance bound at <paramref name="freqHz"/> (-<see cref="ToleranceDb"/> dB).</summary>
    public static double LowerDb(double freqHz) => DbAt(freqHz) - ToleranceDb;
}
