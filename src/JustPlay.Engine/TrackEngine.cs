using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Engine.Dtos;

namespace JustPlay.Engine;

/// <summary>
/// Concrete implementation of <see cref="ITrackEngine"/>. Wires the existing
/// <see cref="ITrackAnalysisService"/>, <see cref="IMetadataReader"/>, and
/// <see cref="IMetadataWriter"/> behind a single high-level surface.
///
/// <para>
/// No analysis or metadata logic lives here - this class purely maps between the
/// Core/Analysis/Metadata domain types and the facade DTOs.  All behaviour changes
/// belong in the underlying services.
/// </para>
///
/// <para>
/// Instantiate via <see cref="TrackEngineBuilder"/> for standalone (CLI/MCP) use,
/// or resolve from a DI container in the App.
/// </para>
/// </summary>
public sealed class TrackEngine : ITrackEngine
{
    private static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".ogg", ".wav", ".aiff", ".aif", ".m4a", ".wv", ".opus"];

    private readonly ITrackAnalysisService _analysis;
    private readonly IMetadataReader _reader;
    private readonly IMetadataWriter _writer;

    /// <summary>
    /// Creates a <see cref="TrackEngine"/> with the supplied service implementations.
    /// The caller (composition root or test) is responsible for wiring the concrete backends.
    /// </summary>
    public TrackEngine(
        ITrackAnalysisService analysis,
        IMetadataReader reader,
        IMetadataWriter writer)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _reader   = reader   ?? throw new ArgumentNullException(nameof(reader));
        _writer   = writer   ?? throw new ArgumentNullException(nameof(writer));
    }

    // -------------------------------------------------------------------------
    // ITrackEngine implementation
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<TrackAnalysisDto> AnalyzeTrackAsync(
        string filePath,
        IProgress<TrackAnalysisDto>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            // Bridge: convert the Core progress<AnalysisResult> to progress<TrackAnalysisDto>
            // so the caller gets typed partial updates that match the facade contract.
            IProgress<AnalysisResult>? innerProgress = progress is null
                ? null
                : new Progress<AnalysisResult>(r =>
                    progress.Report(MapAnalysis(filePath, r, success: true, error: null)));

            var result = await _analysis.AnalyzeAsync(filePath, innerProgress, ct).ConfigureAwait(false);
            return MapAnalysis(filePath, result, success: true, error: null);
        }
        catch (OperationCanceledException)
        {
            throw; // honour cancellation
        }
        catch (Exception ex)
        {
            return new TrackAnalysisDto
            {
                FilePath = filePath,
                Success  = false,
                Error    = ex.Message,
            };
        }
    }

    /// <inheritdoc/>
    public Task<TrackTagsDto> ReadTagsAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // TagLib# is synchronous; offload to avoid blocking the caller's thread.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var meta = _reader.Read(filePath);
            return MapTags(filePath, meta);
        }, ct);
    }

    /// <inheritdoc/>
    public Task<WriteTagsResult> WriteTagsAsync(
        string filePath,
        WriteTagsRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Parse key string if supplied.
                MusicalKey? key = null;
                if (request.Key is { } rawKey)
                {
                    key = MusicalKey.TryParse(rawKey);
                    if (key is null)
                        return new WriteTagsResult
                        {
                            FilePath = filePath,
                            Success  = false,
                            Error    = $"Cannot parse key '{rawKey}'. " +
                                       "Use Camelot (e.g. \"8A\") or musical notation (e.g. \"Am\", \"F#m\").",
                        };
                }

                var tagWrite = new TagWrite
                {
                    Bpm      = request.Bpm,
                    Key      = key,
                    Energy   = request.Energy,
                    Favorite = request.Favorite,
                    Comment  = request.Comment,
                    Grouping = request.Grouping,
                };

                _writer.Write(filePath, tagWrite);

                return new WriteTagsResult { FilePath = filePath, Success = true };
            }
            catch (Exception ex)
            {
                return new WriteTagsResult
                {
                    FilePath = filePath,
                    Success  = false,
                    Error    = ex.Message,
                };
            }
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<LibraryAnalysisResult> AnalyzeLibraryAsync(
        string folder,
        IProgress<TrackAnalysisDto>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var files = Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                // macOS/SMB: protected subfolders (e.g. TemporaryItems, .Trashes) throw
                // UnauthorizedAccess and would abort the whole walk without this.
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
            })
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        var results = new List<TrackAnalysisDto>(files.Count);
        var succeeded = 0;
        var failed    = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var dto = await AnalyzeTrackAsync(file, progress: null, ct).ConfigureAwait(false);
            results.Add(dto);
            progress?.Report(dto);

            if (dto.Success) succeeded++;
            else             failed++;
        }

        return new LibraryAnalysisResult
        {
            Folder     = folder,
            TotalFiles = files.Count,
            Succeeded  = succeeded,
            Failed     = failed,
            Tracks     = results,
        };
    }

    // -------------------------------------------------------------------------
    // Mapping helpers - Core domain -> facade DTOs
    // -------------------------------------------------------------------------

    private static TrackAnalysisDto MapAnalysis(
        string filePath,
        AnalysisResult result,
        bool success,
        string? error) => new()
    {
        FilePath      = filePath,
        Bpm           = result.Bpm,
        KeyName       = result.Key?.Name,
        KeyCamelot    = result.Key?.Camelot,
        KeyConfidence = result.KeyConfidence,
        Energy        = result.Energy,
        Danceability  = result.Fingerprint?.Danceability,
        Success       = success,
        Error         = error,
    };

    private static TrackTagsDto MapTags(string filePath, TrackMetadata meta) => new()
    {
        FilePath          = filePath,
        Title             = meta.Title,
        Artist            = meta.Artist,
        Album             = meta.Album,
        Genre             = meta.Genre,
        Year              = meta.Year,
        Comment           = meta.Comment,
        Grouping          = meta.Grouping,
        DurationSec       = meta.Duration.TotalSeconds,
        BitrateKbps       = meta.Bitrate,
        SampleRate        = meta.SampleRate,
        Channels          = meta.Channels,
        TaggedBpm         = meta.TaggedBpm,
        TaggedKey         = meta.TaggedKey,
        TaggedEnergy      = meta.TaggedEnergy,
        IsFavorite        = meta.IsFavorite,
        HasStoredAnalysis = meta.StoredAnalysis is not null,
    };
}
