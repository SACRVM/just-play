using JustPlay.Engine.Dtos;

namespace JustPlay.Engine;

/// <summary>
/// High-level facade over JustPlay's analysis and metadata stack.
/// Designed for consumption by CLI tools, MCP servers, and any other
/// non-GUI agent that needs to analyse or tag audio files.
///
/// <para>
/// Implementations are injected with the platform-specific backends
/// (e.g. <c>BassBpmDetector</c>, <c>BassAudioDecoder</c>) at the composition root;
/// the interface itself and the DTO layer are platform-agnostic.
/// </para>
///
/// <para>All methods are thread-safe for independent file paths.</para>
/// </summary>
public interface ITrackEngine
{
    /// <summary>
    /// Run the full DSP analysis pipeline on one track (BPM → key → energy → beat fingerprint)
    /// and return the result as a flat DTO.
    /// </summary>
    /// <param name="filePath">Absolute path to the audio file.</param>
    /// <param name="progress">
    /// Optional callback — receives a partial <see cref="TrackAnalysisDto"/> each time
    /// a detector finishes (same contract as <c>ITrackAnalysisService.AnalyzeAsync</c>).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<TrackAnalysisDto> AnalyzeTrackAsync(
        string filePath,
        IProgress<TrackAnalysisDto>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Read tag-level metadata (title, artist, BPM tag, key tag, …) from a file.
    /// Does NOT run DSP analysis; returns whatever is stored in the file's tags.
    /// </summary>
    Task<TrackTagsDto> ReadTagsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Write the supplied fields into the file's tags.  Only non-null fields in
    /// <paramref name="request"/> are written; other tags are left untouched.
    /// </summary>
    Task<WriteTagsResult> WriteTagsAsync(
        string filePath,
        WriteTagsRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Recursively analyse every audio file under <paramref name="folder"/> and
    /// return an aggregate result. Files that fail analysis are reported with
    /// <see cref="TrackAnalysisDto.Success"/> = false and are counted in
    /// <see cref="LibraryAnalysisResult.Failed"/>, but do not abort the batch.
    /// </summary>
    /// <param name="folder">Root directory to scan (recursive).</param>
    /// <param name="progress">Optional per-track progress callback.</param>
    /// <param name="ct">Cancellation token (honours mid-batch cancellation).</param>
    Task<LibraryAnalysisResult> AnalyzeLibraryAsync(
        string folder,
        IProgress<TrackAnalysisDto>? progress = null,
        CancellationToken ct = default);
}
