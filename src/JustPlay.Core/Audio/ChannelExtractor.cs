using System;

namespace JustPlay.Core.Audio;

/// <summary>
/// Pulls a stereo pair out of an interleaved multi-channel float buffer - the operation that lets the
/// per-process "capture a specific APP" source broadcast only a DJ device's Master pair and drop the
/// Cue (see <see cref="AppCaptureFormat"/>). Pure + allocation-free (writes into a caller buffer) so
/// it can run on the capture thread and be unit-tested without any COM/BASS.
/// </summary>
public static class ChannelExtractor
{
    /// <summary>
    /// Copy the channel pair starting at <paramref name="masterOffset"/> from an interleaved
    /// <paramref name="channels"/>-channel buffer into <paramref name="dest"/> as interleaved stereo.
    /// </summary>
    /// <param name="source">Interleaved source PCM (channels x frames).</param>
    /// <param name="validFloats">Number of valid floats in <paramref name="source"/>.</param>
    /// <param name="channels">Channels per frame in the source (>= 2).</param>
    /// <param name="masterOffset">Index of the first channel of the pair to extract. Clamped so the
    /// pair stays within range.</param>
    /// <param name="dest">Destination stereo buffer; grown (reallocated) if too small.</param>
    /// <returns>The number of valid floats written to <paramref name="dest"/> (frames x 2).</returns>
    public static int ToStereoPair(float[] source, int validFloats, int channels, int masterOffset, ref float[] dest)
    {
        if (channels < 2) channels = 2;
        int frames = validFloats / channels;
        if (frames <= 0) return 0;

        // Keep the pair inside the buffer: offset  in  [0, channels-2], even (pairs are L/R aligned).
        if (masterOffset < 0) masterOffset = 0;
        if (masterOffset > channels - 2) masterOffset = channels - 2;

        int need = frames * 2;
        if (dest.Length < need) dest = new float[need];

        for (int f = 0; f < frames; f++)
        {
            int s = f * channels + masterOffset;
            int d = f * 2;
            dest[d] = source[s];
            dest[d + 1] = source[s + 1];
        }
        return need;
    }
}
