using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// What happened to a save the host was asked to carry out.
/// </summary>
public enum TagWriteOutcome
{
    /// <summary>The file was written, right now.</summary>
    Written,

    /// <summary>The file is busy (it is the playing track) — the write was queued and lands later.</summary>
    Deferred,

    /// <summary>The write was attempted and failed; the host reported it.</summary>
    Failed,
}

/// <summary>
/// How a write reaches the file. The editor never opens the file itself — it hands the work to the
/// host, because only the host knows whether this file is currently being played.
/// <para>JUST TAG passes <see cref="TagEditorViewModel.WriteDirect"/>. JUST PLAY passes an executor
/// that routes through its deferred-write queue, so a save onto the PLAYING track is queued and
/// lands at the track change instead of dying on a locked handle (see memory
/// <c>memory-playback-tag-writes</c>).</para>
/// </summary>
public delegate TagWriteOutcome TagWriteExecutor(string filePath, Action<string> write);

/// <summary>
/// The SHARED tag editor — one view model behind two shapes: the floating always-on-top window in
/// JUST PLAY / the PRE CUE FINDER, and JUST TAG's docked sidebar. Written once so the two cannot
/// drift (see memory <c>suite-unify-dont-patch-divergence</c>).
///
/// <para><b>It follows the SELECTION, never playback</b> (Chloe 2026-08-02). If the editor retargeted
/// itself when a track ended, you would be typing an artist name and get a save prompt triggered by
/// an event you did not cause. The host pushes a target; auto-advance must not.</para>
///
/// <para><b>Editorial and analysis fields are two different writes</b>, deliberately:
/// <see cref="IMetadataWriter.WriteEditable"/> never touches BPM/key/energy, and
/// <see cref="IMetadataWriter.Write"/> never touches title/artist/cover. Each runs only if that half
/// actually changed, so editing a title cannot disturb a Serato frame and correcting a key cannot
/// disturb a cover.</para>
///
/// <para>Hand-rolled <see cref="INotifyPropertyChanged"/> on purpose — the shared UI library stays
/// free of a MVVM-toolkit dependency (same rule as <see cref="Logging.LogViewModel"/>).</para>
/// </summary>
public sealed class TagEditorViewModel : INotifyPropertyChanged
{
    private readonly IMetadataReader _reader;
    private readonly IMetadataWriter _writer;
    private readonly TagWriteExecutor _execute;

    /// <summary>The plain executor: write on this thread, right now. JUST TAG's default.</summary>
    public static TagWriteOutcome WriteDirect(string filePath, Action<string> write)
    {
        write(filePath);
        return TagWriteOutcome.Written;
    }

    private readonly Func<IReadOnlyList<string>>? _genreSource;

    public TagEditorViewModel(IMetadataReader reader, IMetadataWriter writer,
                              TagWriteExecutor? execute = null,
                              Func<IReadOnlyList<string>>? genreSource = null)
    {
        _reader = reader;
        _writer = writer;
        _execute = execute ?? WriteDirect;
        _genreSource = genreSource;
    }

    /// <summary>
    /// What the GENRE box suggests: the spellings this library already uses, then the fallback
    /// vocabulary. Resolved once, lazily — the library query is a grouped read over the whole index
    /// and has no business running every time a window opens.
    /// </summary>
    public IReadOnlyList<string> GenreSuggestions =>
        _genres ??= GenreVocabulary.Merge(SafeGenres());

    private IReadOnlyList<string>? _genres;

    private IReadOnlyList<string>? SafeGenres()
    {
        try { return _genreSource?.Invoke(); }
        catch { return null; }   // no index, locked db, anything — fall back to the canned list
    }

    // ── The file ────────────────────────────────────────────────────────────────────────────────

    private string? _filePath;
    public string? FilePath { get => _filePath; private set => Set(ref _filePath, value); }

    private string _fileName = "";
    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }

    private string _fileInfo = "";
    public string FileInfo { get => _fileInfo; private set => Set(ref _fileInfo, value); }

    public bool HasFile => FilePath is not null;

    // ── The file NAME, editable (Chloe 2026-08-03: "dateiname mit reinpacken und änderbar machen") ─
    //
    // Not a tag, but it is the thing you most want to fix while tidying up, and leaving it out would
    // mean switching tools for half the job. The EXTENSION is deliberately not editable: it is shown
    // next to the box and carried over verbatim, so a typo cannot turn a playable file into one
    // Windows no longer opens.

    private string? _baseName;
    public string? BaseName
    {
        get => _baseName;
        set { SetField(ref _baseName, value); Raise(nameof(WillRename)); }
    }

    private string _extension = "";
    public string Extension { get => _extension; private set => Set(ref _extension, value); }

    /// <summary>The file will be renamed on the next save.</summary>
    public bool WillRename =>
        FilePath is not null && !TextEquals(BaseName?.Trim(), _baselineBaseName);

    private string? _baselineBaseName;

    /// <summary>Raised after a rename so hosts can move their row to the new path instead of leaving
    /// it pointing at a file that is no longer there.</summary>
    public event Action<string, string>? Renamed;

    /// <summary>The quick-rename patterns offered next to the name box. Deliberately a short list of
    /// shapes people actually file by — the full mask language (and applying one to a whole folder)
    /// is JUST TAG's job, not this editor's.</summary>
    public static IReadOnlyList<string> RenameMasks =>
    [
        "%artist% - %title%",
        "%title% - %artist%",
        "%artist% - %title% (%album%)",
        "%track% - %artist% - %title%",
    ];

    /// <summary>
    /// Fill the name box from a mask. It only FILLS — nothing is renamed until Save, so the result
    /// is visible and editable first. A placeholder with no value collapses along with the separator
    /// around it, so a missing album does not leave "Artist - Title ()" behind.
    /// </summary>
    public void ApplyRenameMask(string mask)
    {
        if (!HasFile) return;

        var name = mask
            .Replace("%artist%", Blank(Artist) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("%title%", Blank(Title) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("%album%", Blank(Album) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("%albumartist%", Blank(AlbumArtist) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("%genre%", Blank(Genre) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("%year%", Blank(Year) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("%track%", Blank(Track) ?? "", StringComparison.OrdinalIgnoreCase);

        name = TidyName(name);
        if (name.Length == 0)
        {
            Status = "That pattern comes out empty — the tags it needs are blank.";
            return;
        }

        BaseName = name;
        Status = $"Renames to \"{name}{Extension}\" on save.";
    }

    /// <summary>Collapse what empty placeholders leave behind, then strip anything a file name may
    /// not contain. A name is not worth refusing over a colon — fix it and say nothing.</summary>
    private static string TidyName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');

        s = s.Replace("()", "").Replace("[]", "");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        s = s.Trim();
        // A leading or trailing separator is what a missing first/last placeholder leaves.
        s = s.Trim('-', '_', '.', ' ');
        while (s.Contains("- -")) s = s.Replace("- -", "-");
        return s.Trim();
    }

    // ── Editorial fields ────────────────────────────────────────────────────────────────────────

    private string? _title;
    public string? Title { get => _title; set => SetField(ref _title, value); }

    private string? _artist;
    public string? Artist { get => _artist; set => SetField(ref _artist, value); }

    private string? _album;
    public string? Album { get => _album; set => SetField(ref _album, value); }

    private string? _albumArtist;
    public string? AlbumArtist { get => _albumArtist; set => SetField(ref _albumArtist, value); }

    private string? _genre;
    public string? Genre { get => _genre; set => SetField(ref _genre, value); }

    private string? _year;
    public string? Year { get => _year; set => SetField(ref _year, Digits(value)); }

    private string? _track;
    public string? Track { get => _track; set => SetField(ref _track, Digits(value)); }

    private string? _comment;
    public string? Comment { get => _comment; set => SetField(ref _comment, value); }

    // ── Analysis fields, hand-correctable (Chloe 2026-08-02: "ja bpm/key von hand korrigieren") ──
    //
    // These are OUR measurements, so correcting one by hand is a statement, not a typo fix: it says
    // "the detector is wrong on this track". That is recorded as FieldDecision.Kept — the enum's
    // meaning is exactly "the user reviewed this and the tag stands, stop flagging it". The blob's
    // Detected values are NOT overwritten; a measurement stays what it measured, and the correction
    // sits in the standard tag where every other DJ tool reads it.

    private string? _bpm;
    public string? BpmText { get => _bpm; set => SetField(ref _bpm, value); }

    private string? _key;
    public string? KeyText { get => _key; set => SetField(ref _key, value); }

    private string? _energy;
    public string? EnergyText { get => _energy; set => SetField(ref _energy, Digits(value)); }

    /// <summary>What our DSP measured, for the line under the hand-editable fields — so a correction
    /// is made against a visible number instead of blind.</summary>
    private string? _detectedSummary;
    public string? DetectedSummary { get => _detectedSummary; private set => Set(ref _detectedSummary, value); }

    public bool HasDetected => DetectedSummary is not null;

    // ── Cover ───────────────────────────────────────────────────────────────────────────────────

    private Bitmap? _cover;
    public Bitmap? Cover { get => _cover; private set { Set(ref _cover, value); Raise(nameof(HasCover)); } }

    public bool HasCover => Cover is not null;

    private byte[]? _coverBytes;
    private string? _coverMime;
    private CoverAction _coverAction = CoverAction.Keep;

    // ── State ───────────────────────────────────────────────────────────────────────────────────

    private string _status = "No track selected.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private bool _isDirty;
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

    /// <summary>Raised after a successful save so the host can refresh its own row for this file.</summary>
    public event Action<string>? Saved;

    /// <summary>The values as loaded — the yardstick for <see cref="IsDirty"/>. Comparing against a
    /// baseline rather than latching a flag means typing a character and deleting it again leaves
    /// the editor clean, which is what "unsaved changes" should mean.</summary>
    private Snapshot _baseline = Snapshot.Empty;

    private TrackMetadata? _loaded;
    private bool _loading;

    // ── Load ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Point the editor at a file. The caller is responsible for having dealt with
    /// <see cref="IsDirty"/> first — the prompt is the host's, because it needs an owner window.
    /// </summary>
    public void Load(string path)
    {
        _loading = true;
        try
        {
            var m = _reader.Read(path);
            _loaded = m;

            FilePath = path;
            FileName = Path.GetFileName(path);
            FileInfo = BuildInfo(m, path);

            Extension = Path.GetExtension(path);
            _baselineBaseName = Path.GetFileNameWithoutExtension(path);
            BaseName = _baselineBaseName;

            Title       = m.Title;
            Artist      = m.Artist;
            Album       = m.Album;
            AlbumArtist = m.AlbumArtist;
            Genre       = m.Genre;
            Year        = m.Year is > 0 ? m.Year.Value.ToString(CultureInfo.InvariantCulture) : null;
            Track       = m.TrackNumber is > 0 ? m.TrackNumber.Value.ToString(CultureInfo.InvariantCulture) : null;
            Comment     = m.Comment;

            // The tag is what the file SAYS; the blob is what we MEASURED. The editable field shows
            // the tag, because that is the value every other tool reads.
            BpmText    = m.TaggedBpm is > 0 ? m.TaggedBpm.Value.ToString("0.##", CultureInfo.InvariantCulture) : null;
            KeyText    = m.TaggedKey;
            EnergyText = m.TaggedEnergy?.ToString(CultureInfo.InvariantCulture);

            DetectedSummary = DescribeDetected(m.StoredAnalysis?.Detected);
            Raise(nameof(HasDetected));

            SetCoverBytes(m.CoverArt, GuessMime(m.CoverArt));
            _coverAction = CoverAction.Keep;

            _baseline = Snapshot.From(this);
            IsDirty = false;
            Status = FileName;
        }
        catch (Exception ex)
        {
            Status = $"Couldn't read this file — {ex.Message}";
        }
        finally
        {
            _loading = false;
            Raise(nameof(HasFile));
        }
    }

    /// <summary>Drop the editor's target (nothing selected).</summary>
    public void Clear()
    {
        _loading = true;
        FilePath = null; _loaded = null;
        FileName = ""; FileInfo = "";
        Extension = ""; BaseName = null; _baselineBaseName = null;
        Title = Artist = Album = AlbumArtist = Genre = Year = Track = Comment = null;
        BpmText = KeyText = EnergyText = null;
        DetectedSummary = null;
        SetCoverBytes(null, null);
        _coverAction = CoverAction.Keep;
        _baseline = Snapshot.Empty;
        IsDirty = false;
        _loading = false;
        Status = "No track selected.";
        Raise(nameof(HasFile));
        Raise(nameof(HasDetected));
    }

    /// <summary>Throw the edits away and show the file as it is on disk again.</summary>
    public void Revert()
    {
        if (FilePath is { } p) Load(p);
    }

    // ── Cover actions ───────────────────────────────────────────────────────────────────────────

    /// <summary>Replace the cover with picked image bytes (the file picker lives in the view).</summary>
    public void SetNewCover(byte[] bytes, string mime)
    {
        SetCoverBytes(bytes, mime);
        _coverAction = CoverAction.Replace;
        MarkDirty();
        Status = "Cover replaced — not saved yet";
    }

    public void RemoveCover()
    {
        if (!HasFile) return;
        SetCoverBytes(null, null);
        _coverAction = CoverAction.Remove;
        MarkDirty();
        Status = "Cover removed — not saved yet";
    }

    // ── Save ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Write the changed half (or halves) back. Returns false when nothing was written — either
    /// there was nothing to write, or the input could not be parsed (the reason lands in
    /// <see cref="Status"/> rather than being swallowed).
    /// </summary>
    public bool Save()
    {
        if (FilePath is not { } path || _loaded is null) return false;

        var now = Snapshot.From(this);
        var editorialChanged = now.EditorialDiffers(_baseline) || _coverAction != CoverAction.Keep;
        var analysisChanged  = now.AnalysisDiffers(_baseline);
        var rename           = WillRename;

        if (!editorialChanged && !analysisChanged && !rename)
        {
            Status = "Nothing to save.";
            IsDirty = false;
            return false;
        }

        // Check the rename BEFORE writing anything: refusing after the tags have already been
        // written would leave a half-done save, and "the target already exists" is exactly the
        // moment where quietly carrying on would overwrite someone else's file.
        string? newPath = null;
        if (rename)
        {
            var clean = TidyName(BaseName ?? "");
            if (clean.Length == 0)
            {
                Status = "A file needs a name.";
                return false;
            }

            newPath = Path.Combine(Path.GetDirectoryName(path) ?? "", clean + Extension);
            if (!string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
            {
                Status = $"\"{clean}{Extension}\" already exists in this folder.";
                return false;
            }
        }

        // Parse before touching the file: a half-applied save is worse than a refused one.
        double? bpm = null;
        MusicalKey? key = null;
        int? energy = null;

        if (analysisChanged)
        {
            if (!string.IsNullOrWhiteSpace(BpmText))
            {
                if (!double.TryParse(BpmText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b <= 0)
                {
                    Status = $"\"{BpmText}\" is not a BPM.";
                    return false;
                }
                bpm = b;
            }

            if (!string.IsNullOrWhiteSpace(KeyText))
            {
                key = MusicalKey.TryParse(KeyText.Trim());
                if (key is null)
                {
                    Status = $"\"{KeyText}\" is not a key we recognise (try Am, F#m, 8A).";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(EnergyText))
            {
                if (!int.TryParse(EnergyText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)
                    || e is < 1 or > 10)
                {
                    Status = "Energy runs from 1 to 10.";
                    return false;
                }
                energy = e;
            }
        }

        var tags = new EditableTags
        {
            Title       = Blank(Title),
            Artist      = Blank(Artist),
            Album       = Blank(Album),
            AlbumArtist = Blank(AlbumArtist),
            Genre       = Blank(Genre),
            Year        = ParseUint(Year),
            TrackNumber = ParseUint(Track),
            Comment     = Blank(Comment),
        };

        var coverAction = _coverAction;
        var coverBytes  = coverAction == CoverAction.Replace ? _coverBytes : null;
        var coverMime   = coverAction == CoverAction.Replace ? _coverMime : null;
        var write       = analysisChanged ? BuildAnalysisWrite(bpm, key, energy) : null;

        Exception? failure = null;
        var outcome = editorialChanged || analysisChanged
            ? _execute(path, p =>
              {
                  try
                  {
                      if (editorialChanged) _writer.WriteEditable(p, tags, coverAction, coverBytes, coverMime);
                      if (write is not null) _writer.Write(p, write);
                  }
                  catch (Exception ex)
                  {
                      failure = ex;
                      throw;      // the host's executor decides whether this is retried or reported
                  }
              })
            : TagWriteOutcome.Written;   // rename only — nothing to write into the file

        switch (outcome)
        {
            case TagWriteOutcome.Written:
                _coverAction = CoverAction.Keep;      // what we just saved IS the file's cover now
                _baseline = now;
                IsDirty = false;
                Status = "Saved ✓";
                Saved?.Invoke(path);
                if (newPath is not null) TryRename(path, newPath);
                return true;

            case TagWriteOutcome.Deferred:
                // The file is playing. The write is queued, so the editor must NOT claim it landed —
                // but it must also not keep nagging about unsaved changes for work already handed over.
                _coverAction = CoverAction.Keep;
                _baseline = now;
                IsDirty = false;
                // A rename CANNOT ride along: the queued write targets the old path, and the engine
                // is holding the handle anyway. Say so instead of silently dropping half the save.
                Status = newPath is null
                    ? "Track is playing — saves when it changes."
                    : "Track is playing — tags save when it changes, the rename needs it stopped.";
                if (newPath is not null) { BaseName = _baselineBaseName; MarkDirty(); }
                return true;

            default:
                Status = failure is null ? "Save failed." : $"Save failed — {failure.Message}";
                return false;
        }
    }

    /// <summary>
    /// Move the file, after its tags are already written. Renaming LAST is deliberate: a failed
    /// rename then costs nothing (the tag edits are safely on disk), whereas renaming first and
    /// failing the write would leave the file moved and the edits gone.
    /// <para>The editor re-points itself at the new path and tells the host, so no row is left
    /// pointing at a file that is no longer there.</para>
    /// </summary>
    private void TryRename(string from, string to)
    {
        try
        {
            File.Move(from, to);
        }
        catch (Exception ex)
        {
            // The tags DID land — say both halves, or "save failed" would be a lie about the part
            // that worked.
            Status = $"Tags saved, rename failed — {ex.Message}";
            BaseName = _baselineBaseName;
            MarkDirty();
            return;
        }

        FilePath = to;
        FileName = Path.GetFileName(to);
        _baselineBaseName = Path.GetFileNameWithoutExtension(to);
        BaseName = _baselineBaseName;
        IsDirty = false;
        Status = "Saved ✓ · renamed";
        Renamed?.Invoke(from, to);
    }

    /// <summary>
    /// Build the analysis write for a HAND correction.
    ///
    /// <para>The three fields go into the standard tags, and the JUSTPLAY blob records that the user
    /// decided (<see cref="FieldDecision.Kept"/>) — so nothing flags them as pending again, and the
    /// auto-write on the next analysis leaves them alone.</para>
    ///
    /// <para>Two things are deliberately NOT done here: <c>Detected</c> is carried over untouched
    /// (a measurement is not an opinion, and overwriting it would erase the evidence that the
    /// detector was wrong on this track), and <c>Original</c> is left alone — it exists to hold the
    /// foreign value WE overwrote, and a hand correction is not us overwriting anything.</para>
    ///
    /// <para>A file with no blob at all gets no blob from this path: the standard tags are written
    /// and that is it. Stamping a JUSTPLAY record with an empty <c>Detected</c> would claim we
    /// analysed a file we never opened.</para>
    /// </summary>
    private TagWrite BuildAnalysisWrite(double? bpm, MusicalKey? key, int? energy)
    {
        var prev = _loaded?.StoredAnalysis;
        var b = _baseline;

        var bpmChanged    = !TextEquals(BpmText, b.Bpm);
        var keyChanged    = !TextEquals(KeyText, b.Key);
        var energyChanged = !TextEquals(EnergyText, b.Energy);

        return new TagWrite
        {
            Bpm    = bpmChanged ? bpm : null,
            Key    = keyChanged ? key : null,
            Energy = energyChanged ? energy : null,
            State  = prev is null ? null : prev with
            {
                BpmDecision    = bpmChanged    ? FieldDecision.Kept : prev.BpmDecision,
                KeyDecision    = keyChanged    ? FieldDecision.Kept : prev.KeyDecision,
                EnergyDecision = energyChanged ? FieldDecision.Kept : prev.EnergyDecision,
            },
        };
    }

    // ── Dirty tracking ──────────────────────────────────────────────────────────────────────────

    private void MarkDirty()
    {
        if (_loading) return;
        IsDirty = Snapshot.From(this).Differs(_baseline)
               || _coverAction != CoverAction.Keep
               || WillRename;
    }

    /// <summary>The editable values at one moment — the yardstick behind <see cref="IsDirty"/> and
    /// the "which half changed" decision in <see cref="Save"/>.</summary>
    private readonly record struct Snapshot(
        string? Title, string? Artist, string? Album, string? AlbumArtist,
        string? Genre, string? Year, string? Track, string? Comment,
        string? Bpm, string? Key, string? Energy)
    {
        public static readonly Snapshot Empty = new();

        public static Snapshot From(TagEditorViewModel vm) => new(
            vm.Title, vm.Artist, vm.Album, vm.AlbumArtist,
            vm.Genre, vm.Year, vm.Track, vm.Comment,
            vm.BpmText, vm.KeyText, vm.EnergyText);

        public bool EditorialDiffers(Snapshot o) =>
            !TextEquals(Title, o.Title) || !TextEquals(Artist, o.Artist) ||
            !TextEquals(Album, o.Album) || !TextEquals(AlbumArtist, o.AlbumArtist) ||
            !TextEquals(Genre, o.Genre) || !TextEquals(Year, o.Year) ||
            !TextEquals(Track, o.Track) || !TextEquals(Comment, o.Comment);

        public bool AnalysisDiffers(Snapshot o) =>
            !TextEquals(Bpm, o.Bpm) || !TextEquals(Key, o.Key) || !TextEquals(Energy, o.Energy);

        public bool Differs(Snapshot o) => EditorialDiffers(o) || AnalysisDiffers(o);
    }

    /// <summary>Null and "" are the same thing in a tag field — a box the user emptied and a box
    /// that was never set must not read as a change.</summary>
    private static bool TextEquals(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private void SetCoverBytes(byte[]? bytes, string? mime)
    {
        _coverBytes = bytes;
        _coverMime = mime;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                Cover = new Bitmap(ms);
                return;
            }
            catch { /* undecodable image bytes — show no cover rather than crash */ }
        }
        Cover = null;
    }

    private static string? DescribeDetected(AnalysisResult? a)
    {
        if (a is null) return null;
        var parts = new List<string>();
        if (a.Bpm is > 0) parts.Add($"{a.Bpm.Value:0.#} BPM");
        if (a.Key is { } k) parts.Add(k.Camelot is { Length: > 0 } c ? $"{k.Name} · {c}" : k.Name);
        if (a.Energy is { } e) parts.Add($"Energy {e}");
        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }

    private static string? Digits(string? s) =>
        s is null ? null : new string(s.Where(char.IsDigit).ToArray());

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static uint ParseUint(string? s) =>
        uint.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0u;

    private static string BuildInfo(TrackMetadata m, string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var parts = new List<string>();
        if (ext.Length > 0) parts.Add(ext);
        if (m.Duration > TimeSpan.Zero)
            parts.Add(m.Duration.ToString(m.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
        if (m.Bitrate is > 0) parts.Add($"{m.Bitrate} kbps");
        if (m.SampleRate is > 0) parts.Add($"{m.SampleRate / 1000.0:0.#} kHz");
        return string.Join("  ·  ", parts);
    }

    private static string? GuessMime(byte[]? b)
    {
        if (b is null || b.Length < 4) return null;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        return "image/jpeg";
    }

    // ── INotifyPropertyChanged (hand-rolled — no MVVM toolkit in the shared UI library) ──────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    /// <summary>A user-editable field: same as <see cref="Set{T}"/> plus the dirty re-check.</summary>
    private void SetField(ref string? field, string? value, [CallerMemberName] string? name = null)
    {
        if (TextEquals(field, value)) return;
        field = value;
        Raise(name);
        MarkDirty();
    }
}
