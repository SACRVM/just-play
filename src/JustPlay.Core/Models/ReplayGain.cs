namespace JustPlay.Core.Models;

/// <summary>
/// ReplayGain 2.0 math - converts a BS.1770 integrated loudness measurement to a
/// dB gain adjustment that normalises the track to the -18 LUFS reference level.
///
/// <para>Reference: ReplayGain 2.0 specification (David Robinson et al.);
/// -18 LUFS ~ -14 LUFS (EBU R128 broadcast) - 4 LU headroom for consumer use.</para>
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
    /// track's integrated loudness to <see cref="ReferenceLufs"/> (-18 LUFS).
    /// Clamped to +/-51 dB to stay within the standard's legal range.
    /// </summary>
    /// <param name="integratedLufs">BS.1770 integrated loudness in LUFS (negative value).</param>
    /// <returns>Gain in dB - positive means "turn up", negative means "turn down".</returns>
    public static double TrackGainDb(double integratedLufs)
        => Math.Clamp(ReferenceLufs - integratedLufs, -51.0, 51.0);

    /// <summary>
    /// The gain a PLAYER actually applies: re-references the stored -18 ReplayGain to a chosen
    /// playback target (Quiet -19 / Normal -14 / Loud -11), then clip-prevents - a positive gain is
    /// capped so the measured linear <paramref name="peakLinear"/> lands at most at full scale, and
    /// if the peak is unknown a positive gain is suppressed entirely (never amplify blind). At
    /// <paramref name="targetLufs"/> == <see cref="ReferenceLufs"/> this returns the tag gain verbatim.
    /// Shared by the playback engine (PlaybackController) and the GAIN-column display so both agree.
    /// </summary>
    /// <param name="replayGainDb">The stored -18-referenced ReplayGain track gain (dB).</param>
    /// <param name="peakLinear">Measured linear sample peak (0..~1), or null if unknown.</param>
    /// <param name="targetLufs">The playback loudness target in LUFS.</param>
    public static double AppliedGainDb(double replayGainDb, double? peakLinear, double targetLufs)
    {
        var g = replayGainDb + (targetLufs - ReferenceLufs);
        if (peakLinear is { } p && p > 0)
        {
            var peakDb = 20.0 * Math.Log10(p);
            // Cap so the peak lands exactly at 0 dBFS. peakDb == 0 (peak == 1.0, a fully-clipped
            // master) would make -peakDb the IEEE -0.0, which formats as "-0.0" in the GAIN cell -
            // pin it to +0.0 so the display reads a clean "0.0".
            if (g > 0 && peakDb + g > 0) g = peakDb == 0.0 ? 0.0 : -peakDb;
        }
        else if (g > 0)
        {
            g = 0.0;   // peak unknown -> don't amplify blind
        }
        return Math.Clamp(g, -51.0, 51.0);
    }
}
