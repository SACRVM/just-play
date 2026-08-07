using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Measures BS.1770 / EBU R128 K-weighted gated integrated loudness and linear sample peak,
/// producing the values needed to write ReplayGain 2.0 tags (<c>REPLAYGAIN_TRACK_GAIN</c>
/// and <c>REPLAYGAIN_TRACK_PEAK</c>).
///
/// <para><b>Implementation note:</b> runs on the 11 025 Hz energy-decode buffer shared with
/// <see cref="SpectralEnergyDetector"/> - no additional file I/O. Both detectors are called
/// inside <c>TrackAnalysisService</c> on the same <c>energyAudio</c> buffer.</para>
///
/// <para><b>Mono-downmix caveat:</b> the LUFS measurement is based on a mono-downmixed decode.
/// For very wide stereo masters this can read ~1-3 dB below a true stereo BS.1770 measurement
/// (which sums per-channel mean-squares). This is acceptable for ReplayGain normalisation and
/// consistent with how the energy detector is already calibrated. True-peak / stereo BS.1770 is
/// a future upgrade.</para>
///
/// <para>Platform-agnostic, reflection-free, trim/AOT-safe. No NuGet dependencies.</para>
/// </summary>
public sealed class Bs1770LoudnessDetector : ILoudnessDetector
{
    /// <inheritdoc/>
    public LoudnessResult? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        if (audio.Samples is not { Length: > 0 } samples || audio.SampleRate <= 0)
            return null;

        var lufs = Bs1770Loudness.IntegratedLufs(samples, ct);
        if (double.IsNegativeInfinity(lufs) || lufs < -80.0)
            return null;   // silent - no meaningful loudness measurement

        // Linear sample peak - max(|sample|) over the buffer.
        var peak = 0.0;
        foreach (var s in samples)
        {
            var abs = Math.Abs((double)s);
            if (abs > peak) peak = abs;
        }

        return new LoudnessResult(lufs, peak);
    }
}
