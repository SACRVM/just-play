using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Decodes an audio file to raw mono float samples for analysis.
/// Kept separate from <see cref="IAudioEngine"/> so the DSP analyzers depend only on
/// sample data, never on the playback backend.
/// </summary>
public interface IAudioDecoder
{
    /// <summary>
    /// Decode the whole file, downmixed to mono and resampled to
    /// <paramref name="targetSampleRate"/> (e.g. 11025 Hz is plenty for key/BPM).
    /// </summary>
    DecodedAudio DecodeMono(string filePath, int targetSampleRate, CancellationToken ct = default);
}
