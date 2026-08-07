using System.Threading;
using System.Threading.Tasks;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Computes a normalised peak envelope for a file's whole waveform - the data behind the finder's
/// (and, later, JUST SPIN's deck) click-to-seek scrubber. Platform-agnostic: it decodes through
/// <see cref="IAudioDecoder"/> and reduces the samples to buckets, so it carries no BASS/Avalonia
/// dependency. Results are cached by path, so re-cueing the same track paints instantly.
/// </summary>
public interface IWaveformService
{
    /// <summary>
    /// Decode <paramref name="filePath"/> and reduce it to <paramref name="buckets"/> normalised peak
    /// values (0..1, loudest bucket = 1 so quiet tracks still fill the bar height). The heavy decode runs
    /// on a background thread, so callers may await this straight from the UI thread. Honours cancellation
    /// (pass a token that trips when the cue changes); throws on a genuine decode failure.
    /// </summary>
    Task<float[]> ComputeAsync(string filePath, int buckets, CancellationToken ct = default);
}
