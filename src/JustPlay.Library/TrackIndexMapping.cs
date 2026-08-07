using JustPlay.Core.Models;

namespace JustPlay.Library;

/// <summary>
/// The ONE mapping between JustPlay's analysis models and an index entry.
///
/// <para>It used to live inline in the CLI's <c>AnalyzeCommand</c>. It is here now because 0.6
/// has a second producer of entries - <see cref="LibrarySync"/>, which builds them from the blob
/// already stored in a file's tags. Two copies of this mapping would drift the moment a detector
/// gains a field, and then the app and the CLI would disagree about the same track.</para>
/// </summary>
public static class TrackIndexMapping
{
    /// <summary>
    /// Builds an entry from a completed analysis run (what <c>analyze</c> does).
    /// <paramref name="analysis"/> null + <paramref name="error"/> set = the analysis failed;
    /// both null = the file is known but has never been analysed.
    /// </summary>
    public static TrackIndexEntry ToIndexEntry(
        string filePath,
        string contentHash,
        long fileSizeBytes,
        DateTime? modifiedUtc,
        TrackMetadata? meta,
        AnalysisResult? analysis,
        int detectionVersion,
        string? error = null,
        DateTime? analysedAtUtc = null)
    {
        var rhythm = analysis?.Rhythm;
        var fp     = analysis?.Fingerprint;

        return new TrackIndexEntry
        {
            FilePath         = filePath,
            ContentHash      = contentHash,
            AnalysedAt       = TrackIndexEntry.FormatUtc(analysedAtUtc ?? DateTime.UtcNow),
            DetectionVersion = detectionVersion,
            FileSizeBytes    = fileSizeBytes,
            ModifiedUtc      = modifiedUtc is { } m ? TrackIndexEntry.FormatUtc(m) : null,

            Title       = meta?.Title,
            Artist      = meta?.Artist,
            Album       = meta?.Album,
            Genre       = meta?.Genre,
            Year        = meta?.Year,
            DurationSec = meta?.Duration.TotalSeconds ?? 0,
            BitrateKbps = meta?.Bitrate,

            // Everything else a track table shows, so it never has to open the file to paint a row.
            // HasCover is derived from the artwork the reader already resolved through CoverProbe -
            // the SAME decision the tick column and the editor make, so they cannot disagree.
            AlbumArtist = meta?.AlbumArtist,
            Comment     = meta?.Comment,
            TrackNo     = meta?.TrackNumber,
            HasCover    = meta is null ? null : meta.CoverArt is { Length: > 0 },
            Id3Version  = meta?.Id3Version,
            TagRev      = meta is null ? 0 : TrackIndexEntry.CurrentTagRev,

            Success      = analysis is not null && error is null,
            Error        = error,
            Bpm          = analysis?.Bpm,
            KeyName      = analysis?.Key?.Name,
            KeyCamelot   = analysis?.Key?.Camelot,
            KeyConfidence= analysis?.KeyConfidence,
            Energy       = analysis?.Energy,
            LoudnessLufs = analysis?.LoudnessLufs,
            ReplayGainDb = analysis?.ReplayGainDb,
            Peak         = analysis?.Peak,
            Danceability = fp?.Danceability,

            BeatType      = rhythm?.BeatType,
            FourOnFloor   = rhythm?.FourOnFloor,
            OffbeatEnergy = rhythm?.OffbeatEnergy,
            Swing         = rhythm?.Swing,
            Syncopation   = rhythm?.Syncopation,
            HalfTimeFeel  = rhythm?.HalfTimeFeel,

            RawEnergyScore   = analysis?.RawEnergyScore,
            SpectralFlatness = analysis?.SpectralFlatness,
            Harshness        = analysis?.Harshness,
            BassPunch        = analysis?.BassPunch,
            BassGroove       = analysis?.BassGroove,
            Dark             = analysis?.Dark,
            Hypnotic         = analysis?.Hypnotic,

            AcfSharpness     = analysis?.AcfSharpness,
            GridConfidence   = analysis?.GridConfidence,
        };
    }

    /// <summary>
    /// Builds an entry from the analysis ALREADY STORED in the file's JUSTPLAY tag - the whole
    /// point of 0.6's two-machine model: machine B imports what machine A measured, with a tag
    /// read and zero DSP.
    ///
    /// <para>Returns null when the file carries no usable blob; the caller then queues the file
    /// for real analysis. The stored <see cref="TrackAnalysisState.Version"/> becomes the entry's
    /// <see cref="TrackIndexEntry.DetectionVersion"/> - that field finally means something
    /// ((!) entries produced by the CLI carry the legacy constant 1 instead; see
    /// <see cref="TrackIndex.CurrentDetectionVersion"/>).</para>
    ///
    /// <para><b>Analysed-at (post-L6):</b> when the blob itself knows when the DSP ran
    /// (<see cref="TrackAnalysisState.AnalysedAtUtc"/>), the entry records THAT - not the moment
    /// of this import. Older blobs carry no such timestamp; passing
    /// <see cref="TrackIndexEntry.UnknownAnalysedAt"/> instead of leaving it null keeps this an
    /// explicit "unknown" (see that field's doc) rather than falling through to
    /// <see cref="ToIndexEntry"/>'s own <c>?? DateTime.UtcNow</c> default, which is correct for a
    /// genuinely-fresh DSP run but was exactly the bug here: it made an import indistinguishable
    /// from a real measurement.</para>
    /// </summary>
    public static TrackIndexEntry? FromStoredBlob(
        string filePath,
        long fileSizeBytes,
        DateTime modifiedUtc,
        TrackMetadata meta,
        int minVersion = 0)
    {
        if (meta.StoredAnalysis is not { } state) return null;
        if (state.Version < minVersion)             return null;

        return ToIndexEntry(
            filePath,
            // (!!) deliberately NOT hashed: measured 2026-07-30, opening a file over SMB costs
            // ~57 ms against 0.23 ms for its directory entry. Hashing here would read the whole
            // library over the wire to learn nothing the cheap key does not already tell us.
            contentHash: "",
            fileSizeBytes,
            modifiedUtc,
            meta,
            state.Detected,
            detectionVersion: state.Version,
            analysedAtUtc: state.AnalysedAtUtc ?? TrackIndexEntry.UnknownAnalysedAt);
    }

    // -- The other direction: index entry -> the app's models -------------------

    /// <summary>
    /// Rebuilds a full <see cref="AnalysisResult"/> from an index entry - how a reader (the finder
    /// in LIBRARY scope, <c>promote</c>) turns a stored row back into the app's analysis model.
    ///
    /// <para><see cref="AnalysisResult.Fingerprint"/> stays null: the index keeps only the scalar
    /// summary <c>Danceability</c>, not the float arrays a <see cref="BeatFingerprint"/> needs.
    /// Harmonic Sort degrades gracefully when it is absent.</para>
    /// </summary>
    public static AnalysisResult ToAnalysisResult(TrackIndexEntry e)
    {
        MusicalKey? key = null;
        if (e.KeyCamelot is { } cam)
            key = MusicalKey.TryParse(cam);
        else if (e.KeyName is { } name)
            key = MusicalKey.TryParse(name);

        RhythmPattern? rhythm = null;
        if (e.BeatType      is { } bt  &&
            e.FourOnFloor   is { } fof &&
            e.OffbeatEnergy is { } obe &&
            e.Swing         is { } swg &&
            e.Syncopation   is { } syn &&
            e.HalfTimeFeel  is { } htf)
        {
            rhythm = new RhythmPattern
            {
                BeatType      = bt,
                FourOnFloor   = fof,
                OffbeatEnergy = obe,
                Swing         = swg,
                Syncopation   = syn,
                HalfTimeFeel  = htf,
            };
        }

        return new AnalysisResult
        {
            Bpm              = e.Bpm,
            Key              = key,
            KeyConfidence    = e.KeyConfidence,
            Energy           = e.Energy,
            LoudnessLufs     = e.LoudnessLufs,
            ReplayGainDb     = e.ReplayGainDb,
            Peak             = e.Peak,
            Fingerprint      = null,
            Rhythm           = rhythm,
            RawEnergyScore   = e.RawEnergyScore,
            SpectralFlatness = e.SpectralFlatness,
            Harshness        = e.Harshness,
            BassPunch        = e.BassPunch,
            BassGroove       = e.BassGroove,
            Dark             = e.Dark,
            Hypnotic         = e.Hypnotic,
            AcfSharpness     = e.AcfSharpness,
            GridConfidence   = e.GridConfidence,
        };
    }

    /// <summary>
    /// The tag-level view of an index entry, so a row can be shown WITHOUT opening the file.
    /// This is the finder's whole speed-up in LIBRARY scope: 0 ms per row instead of ~57 ms.
    /// </summary>
    public static TrackMetadata ToMetadata(TrackIndexEntry e) => new()
    {
        FallbackName = Path.GetFileNameWithoutExtension(e.FilePath),
        Title        = e.Title,
        Artist       = e.Artist,
        Album        = e.Album,
        Genre        = e.Genre,
        Year         = e.Year,
        Duration     = TimeSpan.FromSeconds(e.DurationSec),
        Bitrate      = e.BitrateKbps,
        TaggedBpm    = e.Bpm is { } bpm ? (uint)Math.Round(bpm) : null,
        TaggedKey    = e.KeyCamelot,
        TaggedEnergy = e.Energy,

        // The columns that used to cost a live tag read (album artist, comment) or a whole extra
        // file open per visible row (the COV tick, the ID3 version) now come straight off the row.
        AlbumArtist  = e.AlbumArtist,
        Comment      = e.Comment,
        TrackNumber  = e.TrackNo,
        Id3Version   = e.Id3Version,
        // (!) NOT the artwork itself - the index stores WHETHER there is one, never the bytes. A list
        // shows a tick; only the editor decodes a picture, and it does that for ONE file.
        CoverArt     = null,
    };

    /// <summary>
    /// A tag-only entry for a file we know about but have never analysed
    /// (<c>Success=false</c>, <c>Error=null</c>). It goes into the index on purpose: a track that
    /// is on disk must be visible in the library even before its DSP has run - never silently
    /// leave a song behind.
    /// </summary>
    public static TrackIndexEntry NotAnalysed(
        string filePath, long fileSizeBytes, DateTime modifiedUtc, TrackMetadata? meta) =>
        ToIndexEntry(
            filePath, contentHash: "", fileSizeBytes, modifiedUtc, meta,
            analysis: null, detectionVersion: 0, error: null);
}
