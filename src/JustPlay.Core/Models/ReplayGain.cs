namespace JustPlay.Core.Models;

/// <summary>
/// ReplayGain 2.0 math — converts a BS.1770 integrated loudness measurement to a
/// dB gain adjustment that normalises the track to the −18 LUFS reference level.
///
/// <para>Reference: ReplayGain 2.0 specification (David Robinson et al.);
/// −18 LUFS ≈ −14 LUFS (EBU R128 broadcast) − 4 LU headroom for consumer use.</para>
///
/// <para>The gain is written to the standard <c>REPLAYGAIN_TRACK_GAIN</c> tag as a dB
/// value with two decimal places (e.g. <c>"-6.35 dB"</c>).</para>
/// </summary>
public static class ReplayGain
{
    /// <summary>ReplayGain 2.0 reference loudness level (LUFS).</summary>
    public const double ReferenceLufs = -18.0;

    /// <summary>
    /// Computes the ReplayGain 2.0 track gain: the dB adjustment required to bring the
    /// track's integrated loudness to <see cref="ReferenceLufs"/> (−18 LUFS).
    /// Clamped to ±51 dB to stay within the standard's legal range.
    /// </summary>
    /// <param name="integratedLufs">BS.1770 integrated loudness in LUFS (negative value).</param>
    /// <returns>Gain in dB — positive means "turn up", negative means "turn down".</returns>
    public static double TrackGainDb(double integratedLufs)
        => Math.Clamp(ReferenceLufs - integratedLufs, -51.0, 51.0);
}
