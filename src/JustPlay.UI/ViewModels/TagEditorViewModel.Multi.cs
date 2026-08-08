using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// One editable thing in the tag editor. A flag SET, because multi-file editing talks about groups
/// of fields constantly - which ones you ticked, which ones the selection already agrees on, which
/// ones a save is about to write - and a dozen loose bools is not something you can pass around.
/// </summary>
[Flags]
public enum TagField
{
    None        = 0,
    Title       = 1 << 0,
    Artist      = 1 << 1,
    Album       = 1 << 2,
    AlbumArtist = 1 << 3,
    Genre       = 1 << 4,
    Year        = 1 << 5,
    Track       = 1 << 6,
    Comment     = 1 << 7,
    Bpm         = 1 << 8,
    Key         = 1 << 9,
    Energy      = 1 << 10,

    /// <summary>The embedded picture. Not text, and compared by bytes rather than by string.</summary>
    Cover       = 1 << 11,

    /// <summary>The name on disk. Across a selection it is a MASK, not a name.</summary>
    Name        = 1 << 12,

    All = Title | Artist | Album | AlbumArtist | Genre | Year | Track | Comment
        | Bpm | Key | Energy | Cover | Name,

    /// <summary>The fields that go through <see cref="IMetadataWriter.WriteEditable"/>.</summary>
    Editorial = Title | Artist | Album | AlbumArtist | Genre | Year | Track | Comment,

    /// <summary>The three hand-correctable measurements - a different write, and a different meaning.</summary>
    Analysis = Bpm | Key | Energy,
}

/// <summary>What the picture will do on the next save. Internal bookkeeping: the panel shows this the
/// same way it shows a text field, through the tick and the field's own contents.</summary>
public enum CoverPlan
{
    /// <summary>One file, or the whole selection carries the same picture.</summary>
    Same,

    /// <summary>The selection carries different pictures.</summary>
    Differs,

    /// <summary>Still reading the pictures to find out.</summary>
    Comparing,

    /// <summary>A picture was chosen; it goes into every selected file.</summary>
    Replace,

    /// <summary>The picture was cleared; it comes out of every selected file.</summary>
    Remove,
}

/// <summary>
/// One file taking part in a multi-file edit: where it is, and what its tags said when the selection
/// was made.
/// <para>The tags come off the host's ROW, not off the file. The index carries the editorial fields
/// (schema v3), so answering "do these 37 files agree on their genre?" opens nothing at all - the
/// standing rule for anything the UI shows. The picture is the one exception, and is handled as
/// one.</para>
/// </summary>
public sealed record TagTarget(string Path, TrackMetadata? Tags);

/// <summary>One line of the rename preview: what a file is called, and what the mask would call it.</summary>
public sealed record RenamePreviewRow(string From, string To, RenameIssue Issue)
{
    public bool IsProblem => Issue != RenameIssue.None;

    public string IssueText => Issue switch
    {
        RenameIssue.Collides => "two files would get this name",
        RenameIssue.Exists   => "that name is already taken here",
        RenameIssue.Empty    => "the tags this pattern needs are blank",
        _                    => "",
    };
}

/// <summary>Why a previewed rename cannot go ahead. <see cref="None"/> is the good case.</summary>
public enum RenameIssue { None, Collides, Exists, Empty }

/// <summary>
/// What a save is about to do, worked out BEFORE it runs so it can be shown and refused.
/// <para>Writing tags into dozens of files cannot be taken back, so "what is about to happen" has to
/// exist as a value the UI can render - not as a sentence in a code comment.</para>
/// </summary>
public sealed record TagSavePlan(
    int FileCount,
    IReadOnlyList<TagSetting> Sets,
    string? RenameMask,
    CoverPlan Cover)
{
    public bool ChangesCover => Cover is CoverPlan.Replace or CoverPlan.Remove;

    /// <summary>Would this save touch anything at all?</summary>
    public bool HasWork => Sets.Count > 0 || RenameMask is not null || ChangesCover;
}

/// <summary>One line of the confirmation: this field, set to this value, on every selected file.</summary>
/// <param name="Clears">The value is being emptied - the one setting that can lose data outright.</param>
public sealed record TagSetting(string Field, string Value, bool Clears);

/// <summary>How a multi-file save actually went.</summary>
public sealed record TagSaveReport(int Written, int Unchanged, int Deferred, int Failed,
                                   string? FirstError);

public sealed partial class TagEditorViewModel
{
    // ============================================================================================
    // MULTI-FILE EDITING
    //
    // Same panel, one addition: every field grows a TICK on its left and an X on its right, inside
    // the box.
    //
    //   tick = this field takes part in the save. Off, and the field is read-only - a box that
    //          cannot be written should not invite typing into it.
    //   X    = empty it. And emptying is an intent, so the X ticks the field itself.
    //
    // That makes one rule cover everything: TICKED AND EMPTY MEANS "CLEAR THIS ON ALL OF THEM" -
    // for a text field and for the picture alike. The picture needed no vocabulary of its own; it
    // needed the same two controls (Chloe 2026-08-07: "dann waere das verhalten gleich").
    //
    // The tick starts ON for a field the whole selection agrees on (the box shows that shared value,
    // editable in one go) and OFF for one where they differ (the box is empty and says so, and
    // nothing gets flattened by accident).
    // ============================================================================================

    private IReadOnlyList<TagTarget> _targets = [];
    private TagField _write = TagField.All;
    private TagField _uniform = TagField.None;
    private CancellationTokenSource? _coverCompare;

    /// <summary>More than one file is loaded, so the ticks are live.</summary>
    public bool IsMulti => _targets.Count > 1;

    /// <summary>How many files the editor is pointed at.</summary>
    public int FileCount => _targets.Count > 0 ? _targets.Count : FilePath is null ? 0 : 1;

    /// <summary>ANALYSIS is a per-file measurement and has nothing to say about a selection, so the
    /// hosts hide the tab rather than show an empty one.</summary>
    public bool CanShowAnalysis => !IsMulti;

    /// <summary>The files the editor is holding, as a set - what a host compares against to tell
    /// whether a new selection is actually a different one.</summary>
    public IReadOnlySet<string> SelectedPaths =>
        _paths ??= _targets.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private HashSet<string>? _paths;

    private CoverPlan _coverPlan = CoverPlan.Same;

    /// <summary>What the picture will do on save. Set by picking one, by clearing it, or by the
    /// background comparison that decides whether the selection already shares one.</summary>
    public CoverPlan CoverState
    {
        get => _coverPlan;
        private set
        {
            Set(ref _coverPlan, value);
            Raise(nameof(CoverHint));
            Raise(nameof(HasCoverHint));
            Raise(nameof(ShowNoCover));
        }
    }

    /// <summary>The picture slot has something to say instead of showing a picture.</summary>
    public bool HasCoverHint => CoverHint.Length > 0;

    /// <summary>"No cover" - the ONE file case, and only when there is no other line to show.</summary>
    public bool ShowNoCover => !HasCover && !HasCoverHint;

    // -- The ticks -------------------------------------------------------------------------------
    //
    // Backed by ONE flag set rather than a dozen bools: a save has to state WHICH FIELDS, and a mask
    // is that sentence. In single-file mode every getter reports true, so the panel's read-only
    // bindings and the existing save path behave exactly as they always did.

    private bool Ticked(TagField f) => !IsMulti || (_write & f) != 0;

    private void Tick(TagField f, bool on, [CallerMemberName] string? name = null)
    {
        if (!IsMulti) return;

        var next = on ? _write | f : _write & ~f;
        if (next == _write) return;

        _write = next;
        Raise(name);
        Raise(HintNameFor(f));
        MarkDirty();
    }

    public bool WriteTitle       { get => Ticked(TagField.Title);       set => Tick(TagField.Title, value); }
    public bool WriteArtist      { get => Ticked(TagField.Artist);      set => Tick(TagField.Artist, value); }
    public bool WriteAlbum       { get => Ticked(TagField.Album);       set => Tick(TagField.Album, value); }
    public bool WriteAlbumArtist { get => Ticked(TagField.AlbumArtist); set => Tick(TagField.AlbumArtist, value); }
    public bool WriteGenre       { get => Ticked(TagField.Genre);       set => Tick(TagField.Genre, value); }
    public bool WriteYear        { get => Ticked(TagField.Year);        set => Tick(TagField.Year, value); }
    public bool WriteTrack       { get => Ticked(TagField.Track);       set => Tick(TagField.Track, value); }
    public bool WriteComment     { get => Ticked(TagField.Comment);     set => Tick(TagField.Comment, value); }
    public bool WriteBpm         { get => Ticked(TagField.Bpm);         set => Tick(TagField.Bpm, value); }
    public bool WriteKey         { get => Ticked(TagField.Key);         set => Tick(TagField.Key, value); }
    public bool WriteEnergy      { get => Ticked(TagField.Energy);      set => Tick(TagField.Energy, value); }
    public bool WriteName        { get => Ticked(TagField.Name);        set => Tick(TagField.Name, value); }

    /// <summary>
    /// The picture's tick. Unticking is the way BACK out of a removal or a replacement - the same
    /// exit "Original name" gives a rename, so changing your mind never costs the other edits.
    /// </summary>
    public bool WriteCover
    {
        get => Ticked(TagField.Cover);
        set
        {
            if (!value && CoverState is CoverPlan.Remove or CoverPlan.Replace)
            {
                _coverAction = CoverAction.Keep;
                RestoreCommonCover();
            }
            Tick(TagField.Cover, value);
        }
    }

    // -- What an empty box means -----------------------------------------------------------------

    /// <summary>
    /// The grey line inside an empty field. It appears ONLY while the tick is off - that is, only
    /// while nothing is going to happen to that field. Tick it and the box is plainly empty, because
    /// from that moment empty is a value: it will be written.
    /// </summary>
    private string Hint(TagField f, string what) =>
        IsMulti && !Ticked(f) && (_uniform & f) == 0 ? what : "";

    public string TitleHint       => Hint(TagField.Title, DifferentValues);
    public string ArtistHint      => Hint(TagField.Artist, DifferentValues);
    public string AlbumHint       => Hint(TagField.Album, DifferentValues);
    public string AlbumArtistHint => Hint(TagField.AlbumArtist, DifferentValues);
    public string GenreHint       => Hint(TagField.Genre, DifferentValues);
    public string YearHint        => Hint(TagField.Year, Different);
    public string TrackHint       => Hint(TagField.Track, Different);
    public string CommentHint     => Hint(TagField.Comment, DifferentValues);
    public string BpmHint         => Hint(TagField.Bpm, Different);
    public string KeyHint         => Hint(TagField.Key, Different);
    public string EnergyHint      => Hint(TagField.Energy, Different);

    /// <summary>The picture's version of the same line - plus the one transient state it has, while
    /// the bytes are still being read.</summary>
    public string CoverHint => !IsMulti
        ? ""
        : CoverState switch
          {
              CoverPlan.Comparing => "comparing...",
              CoverPlan.Differs when !WriteCover => "different covers",
              _ => "",
          };

    private const string DifferentValues = "different values";
    private const string Different = "different";

    private static string HintNameFor(TagField f) => f switch
    {
        TagField.Title       => nameof(TitleHint),
        TagField.Artist      => nameof(ArtistHint),
        TagField.Album       => nameof(AlbumHint),
        TagField.AlbumArtist => nameof(AlbumArtistHint),
        TagField.Genre       => nameof(GenreHint),
        TagField.Year        => nameof(YearHint),
        TagField.Track       => nameof(TrackHint),
        TagField.Comment     => nameof(CommentHint),
        TagField.Bpm         => nameof(BpmHint),
        TagField.Key         => nameof(KeyHint),
        TagField.Energy      => nameof(EnergyHint),
        TagField.Cover       => nameof(CoverHint),
        _                    => nameof(FileCount),   // no hint of its own; harmless to re-raise
    };

    // -- Clearing --------------------------------------------------------------------------------

    /// <summary>
    /// The X inside a field: empty it, and tick it. Two halves of one gesture - you do not clear a
    /// field you did not mean to write, so pressing X IS the decision to write it.
    /// <para>The picture uses this too, with <see cref="TagField.Cover"/>, which is what makes the
    /// two behave the same.</para>
    /// </summary>
    public void ClearField(TagField f)
    {
        switch (f)
        {
            case TagField.Title:       Title = null; break;
            case TagField.Artist:      Artist = null; break;
            case TagField.Album:       Album = null; break;
            case TagField.AlbumArtist: AlbumArtist = null; break;
            case TagField.Genre:       Genre = null; break;
            case TagField.Year:        Year = null; break;
            case TagField.Track:       Track = null; break;
            case TagField.Comment:     Comment = null; break;
            case TagField.Bpm:         BpmText = null; break;
            case TagField.Key:         KeyText = null; break;
            case TagField.Energy:      EnergyText = null; break;
            case TagField.Cover:       RemoveCover(); break;
            default: return;
        }

        Tick(f, true, WriteNameFor(f));
        MarkDirty();
    }

    private static string WriteNameFor(TagField f) => f switch
    {
        TagField.Title       => nameof(WriteTitle),
        TagField.Artist      => nameof(WriteArtist),
        TagField.Album       => nameof(WriteAlbum),
        TagField.AlbumArtist => nameof(WriteAlbumArtist),
        TagField.Genre       => nameof(WriteGenre),
        TagField.Year        => nameof(WriteYear),
        TagField.Track       => nameof(WriteTrack),
        TagField.Comment     => nameof(WriteComment),
        TagField.Bpm         => nameof(WriteBpm),
        TagField.Key         => nameof(WriteKey),
        TagField.Energy      => nameof(WriteEnergy),
        TagField.Cover       => nameof(WriteCover),
        _                    => nameof(WriteName),
    };

    // -- The rename mask -------------------------------------------------------------------------

    private string? _renameMask;

    /// <summary>
    /// Across a selection the name box is a PATTERN, not a name - there is no one name that fits 37
    /// files. Picking one fills nothing in: it is resolved per file against that file's own tags, and
    /// you read the result in the preview before anything moves.
    /// </summary>
    public string? RenameMask
    {
        get => _renameMask;
        set
        {
            Set(ref _renameMask, value);
            Raise(nameof(RenameMaskLabel));
            Raise(nameof(HasRenameMask));
            Raise(nameof(RenameTickTip));
            Raise(nameof(RenamePreviewTip));
            // Picking a pattern IS the decision to rename, so it ticks its own box - the same way
            // choosing a picture ticks the cover and the X ticks a text field.
            Tick(TagField.Name, value is { Length: > 0 }, nameof(WriteName));
            MarkDirty();
        }
    }

    /// <summary>What the picker shows when it is closed. Deliberately the RAW pattern rather than a
    /// friendly name: the placeholders are the thing you are choosing, and seeing them is what makes
    /// the preview's result predictable instead of surprising.</summary>
    public string RenameMaskLabel => HasRenameMask ? RenameMask! : "Leave names alone";

    /// <summary>
    /// A pattern is chosen. The tick and the eye both hang off this, because without a pattern
    /// neither has anything to act on: a ticked FILE NAME with no pattern would claim the files get
    /// renamed and then rename nothing, and the eye would open a preview of nothing. Both used to be
    /// reachable, and both failed in silence.
    /// </summary>
    public bool HasRenameMask => RenameMask is { Length: > 0 };

    /// <summary>Why the tick is greyed, rather than leaving you to work it out. A disabled control
    /// that says nothing is the same dead end as one that silently does nothing.</summary>
    public string RenameTickTip => HasRenameMask
        ? "Rename the files when saving"
        : "Pick a pattern first - there is no name to give them yet";

    public string RenamePreviewTip => HasRenameMask
        ? "Preview every new name before anything is renamed"
        : "Pick a pattern first - there is nothing to preview yet";

    // -- Loading a selection ---------------------------------------------------------------------

    /// <summary>
    /// Point the editor at a whole selection. One file falls straight through to <see cref="Load"/>,
    /// so the ordinary case has no second code path to drift.
    /// </summary>
    public void LoadMany(IReadOnlyList<TagTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        CancelCoverCompare();

        _paths = null;

        if (targets.Count == 0) { Clear(); return; }
        if (targets.Count == 1) { _targets = []; Load(targets[0].Path); return; }

        _loading = true;
        try
        {
            _targets = targets;
            _loaded = null;
            FilePath = null;                    // there is no ONE file, and nothing may assume there is
            FileName = $"{targets.Count} files";
            FileInfo = $"{targets.Count} files selected";
            Extension = "";
            BaseName = null;
            _baselineBaseName = null;

            // The PATTERN survives a new selection on purpose - picking one and walking it through
            // folder after folder is the actual workflow, and re-choosing it every time is friction
            // for nothing. What does NOT survive is its tick: _write below drops Name, so the pattern
            // is offered, never armed. Carrying a live rename into a selection you just made would be
            // the one way this could surprise someone.
            Raise(nameof(RenameMaskLabel));
            Raise(nameof(HasRenameMask));
            Raise(nameof(RenameTickTip));
            Raise(nameof(RenamePreviewTip));

            var tags = targets.Select(t => t.Tags).ToList();

            _uniform = TagField.None;
            Title       = Common(tags, m => m.Title, TagField.Title);
            Artist      = Common(tags, m => m.Artist, TagField.Artist);
            Album       = Common(tags, m => m.Album, TagField.Album);
            AlbumArtist = Common(tags, m => m.AlbumArtist, TagField.AlbumArtist);
            Genre       = Common(tags, m => m.Genre, TagField.Genre);
            Year        = Common(tags, m => Num(m.Year), TagField.Year);
            Track       = Common(tags, m => Num(m.TrackNumber), TagField.Track);
            Comment     = Common(tags, m => m.Comment, TagField.Comment);
            BpmText     = Common(tags, m => m.TaggedBpm is > 0
                                          ? m.TaggedBpm.Value.ToString("0.##", CultureInfo.InvariantCulture)
                                          : null, TagField.Bpm);
            KeyText     = Common(tags, m => m.TaggedKey, TagField.Key);
            EnergyText  = Common(tags, m => m.TaggedEnergy?.ToString(CultureInfo.InvariantCulture),
                                 TagField.Energy);

            // Agreed fields start ticked and show their shared value; disagreeing ones start unticked
            // and empty. NAME and COVER are never ticked by default - neither is something to drift
            // into, and both have their own gesture to opt in with.
            _write = _uniform & ~TagField.Name & ~TagField.Cover;

            DetectedSummary = null;
            BuildMeasurements(null);
            SetCoverBytes(null, null);
            _coverAction = CoverAction.Keep;
            CoverState = CoverPlan.Comparing;

            _baseline = Snapshot.From(this);
            IsDirty = false;
            Status = "";
        }
        finally
        {
            _loading = false;
            Raise(nameof(HasFile));
            RaiseMultiState();
        }

        StartCoverCompare(targets);
    }

    /// <summary>The shared value, or null when they differ. A field they all agree on is recorded in
    /// <see cref="_uniform"/>, which decides the tick AND what "already correct everywhere" means at
    /// save time.</summary>
    private string? Common(IReadOnlyList<TrackMetadata?> tags, Func<TrackMetadata, string?> pick,
                           TagField field)
    {
        string? first = null;

        for (var i = 0; i < tags.Count; i++)
        {
            var m = tags[i];
            var v = m is null ? null : Blank(pick(m));
            if (i == 0) { first = v; continue; }
            if (!TextEquals(first, v)) return null;      // they differ - the box stays empty
        }

        _uniform |= field;
        return first;
    }

    private static string? Num(uint? v) =>
        v is > 0 ? v.Value.ToString(CultureInfo.InvariantCulture) : null;

    private void RaiseMultiState()
    {
        Raise(nameof(IsMulti));
        Raise(nameof(FileCount));
        Raise(nameof(CanShowAnalysis));
        Raise(nameof(RenameMask));
        Raise(nameof(RenameMaskLabel));
        Raise(nameof(HasRenameMask));
        Raise(nameof(RenameTickTip));
        Raise(nameof(RenamePreviewTip));
        Raise(nameof(CoverHint));

        foreach (var f in AllFields)
        {
            Raise(WriteNameFor(f));
            Raise(HintNameFor(f));
        }
    }

    private static readonly TagField[] AllFields =
    [
        TagField.Title, TagField.Artist, TagField.Album, TagField.AlbumArtist, TagField.Genre,
        TagField.Year, TagField.Track, TagField.Comment, TagField.Bpm, TagField.Key,
        TagField.Energy, TagField.Cover, TagField.Name,
    ];

    // -- Cover comparison ------------------------------------------------------------------------

    /// <summary>
    /// The one question a selection cannot answer from the index: it knows WHETHER a file has a
    /// picture, not WHICH. So the bytes are read in the background and compared by length first and a
    /// hash second - length alone settles nearly every "these differ" case without hashing anything.
    /// <para>Cancelled and restarted whenever the selection changes, so clicking through a list can
    /// never let an older pass land on top of a newer one.</para>
    /// </summary>
    private void StartCoverCompare(IReadOnlyList<TagTarget> targets)
    {
        CancelCoverCompare();

        var cts = new CancellationTokenSource();
        _coverCompare = cts;
        var token = cts.Token;

        _ = Task.Run(() =>
        {
            byte[]? shared = null;
            string? sharedHash = null;
            var same = true;

            for (var i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) return;

                byte[]? bytes;
                try { bytes = _reader.Read(targets[i].Path).CoverArt; }
                catch { same = false; break; }      // unreadable: do not claim they match

                if (i == 0) { shared = bytes; sharedHash = Hash(bytes); continue; }

                if ((bytes?.Length ?? 0) != (shared?.Length ?? 0) ||
                    !string.Equals(Hash(bytes), sharedHash, StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (token.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || CoverState != CoverPlan.Comparing) return;

                if (same)
                {
                    _uniform |= TagField.Cover;
                    SetCoverBytes(shared, GuessMime(shared));
                    CoverState = CoverPlan.Same;
                }
                else
                {
                    _uniform &= ~TagField.Cover;
                    SetCoverBytes(null, null);
                    CoverState = CoverPlan.Differs;
                }
            });
        }, token);
    }

    private void CancelCoverCompare()
    {
        _coverCompare?.Cancel();
        _coverCompare?.Dispose();
        _coverCompare = null;
    }

    private static string? Hash(byte[]? bytes) =>
        bytes is { Length: > 0 } ? Convert.ToHexString(SHA256.HashData(bytes)) : null;

    /// <summary>Put the selection's shared picture back after a removal or replacement is undone.</summary>
    private void RestoreCommonCover()
    {
        if (!IsMulti) return;
        SetCoverBytes(null, null);
        CoverState = CoverPlan.Comparing;
        StartCoverCompare(_targets);
    }

    // -- The plan --------------------------------------------------------------------------------

    /// <summary>
    /// Work out what a save would do, without doing it. The panel asks it whether Save has anything
    /// to light up for; the host shows it as the confirmation. The same value both times, so what you
    /// are told is exactly what runs.
    /// </summary>
    public TagSavePlan BuildPlan()
    {
        var sets = new List<TagSetting>();

        void Add(TagField f, string label, string? value)
        {
            if (!Ticked(f)) return;

            // A field the selection already agrees on, left untouched, writes nothing - so it is not
            // part of the plan either. Anything else either unifies them or clears them.
            if ((_uniform & f) != 0 && TextEquals(value, BaselineOf(f))) return;

            var blank = string.IsNullOrWhiteSpace(value);
            sets.Add(new TagSetting(label, blank ? "" : value!.Trim(), blank));
        }

        Add(TagField.Title, "TITLE", Title);
        Add(TagField.Artist, "ARTIST", Artist);
        Add(TagField.Album, "ALBUM", Album);
        Add(TagField.AlbumArtist, "ALBUM ARTIST", AlbumArtist);
        Add(TagField.Genre, "GENRE", Genre);
        Add(TagField.Year, "YEAR", Year);
        Add(TagField.Track, "TRACK #", Track);
        Add(TagField.Comment, "COMMENT", Comment);
        Add(TagField.Bpm, "BPM", BpmText);
        Add(TagField.Key, "KEY", KeyText);
        Add(TagField.Energy, "ENERGY", EnergyText);

        var mask = Ticked(TagField.Name) && !string.IsNullOrWhiteSpace(RenameMask) ? RenameMask : null;
        var cover = Ticked(TagField.Cover) ? CoverState : CoverPlan.Same;

        return new TagSavePlan(FileCount, sets, mask, cover);
    }

    private string? BaselineOf(TagField f) => f switch
    {
        TagField.Title       => _baseline.Title,
        TagField.Artist      => _baseline.Artist,
        TagField.Album       => _baseline.Album,
        TagField.AlbumArtist => _baseline.AlbumArtist,
        TagField.Genre       => _baseline.Genre,
        TagField.Year        => _baseline.Year,
        TagField.Track       => _baseline.Track,
        TagField.Comment     => _baseline.Comment,
        TagField.Bpm         => _baseline.Bpm,
        TagField.Key         => _baseline.Key,
        TagField.Energy      => _baseline.Energy,
        _                    => null,
    };

    // -- The rename preview ----------------------------------------------------------------------

    /// <summary>
    /// Resolve the mask against every selected file and say what would happen - including the two
    /// ways it goes wrong: two files landing on one name, and a name that is already taken.
    /// <para>It costs nothing: the tags it needs came off the rows, so a 500-file preview opens no
    /// file. That is what makes it affordable to show BEFORE the button instead of offering an undo
    /// after it.</para>
    /// </summary>
    public IReadOnlyList<RenamePreviewRow> PreviewRenames()
    {
        if (RenameMask is not { Length: > 0 } mask || _targets.Count == 0) return [];

        var rows = new List<RenamePreviewRow>(_targets.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in _targets)
        {
            var from = Path.GetFileName(t.Path);
            var ext = Path.GetExtension(t.Path);
            var name = TidyName(Resolve(mask, t.Tags));

            if (name.Length == 0)
            {
                rows.Add(new RenamePreviewRow(from, "", RenameIssue.Empty));
                continue;
            }

            var to = name + ext;
            var full = Path.Combine(Path.GetDirectoryName(t.Path) ?? "", to);

            var issue = RenameIssue.None;
            if (!taken.Add(full)) issue = RenameIssue.Collides;
            else if (!string.Equals(full, t.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                issue = RenameIssue.Exists;

            rows.Add(new RenamePreviewRow(from, to, issue));
        }

        // A collision has TWO sides - the first file was still innocent when it was added, so the
        // names that collided are collected and everyone carrying one is marked.
        var clashing = rows.Where(r => r.Issue == RenameIssue.Collides)
                           .Select(r => r.To)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return clashing.Count == 0
            ? rows
            : rows.Select(r => r.Issue == RenameIssue.None && clashing.Contains(r.To)
                               ? r with { Issue = RenameIssue.Collides }
                               : r).ToList();
    }

    /// <summary>The mask resolved against ONE file's tags. Same placeholders as the single-file quick
    /// rename, so a pattern means the same thing wherever it is used.</summary>
    private static string Resolve(string mask, TrackMetadata? m) => mask
        .Replace("%artist%", m?.Artist ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("%title%", m?.Title ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("%album%", m?.Album ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("%albumartist%", m?.AlbumArtist ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("%genre%", m?.Genre ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("%year%", Num(m?.Year) ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("%track%", Num(m?.TrackNumber) ?? "", StringComparison.OrdinalIgnoreCase);

    // -- The save --------------------------------------------------------------------------------

    /// <summary>
    /// Write the ticked fields into every selected file.
    ///
    /// <para><b>Read, overlay, write</b> - one file at a time, and it has to be that way: the
    /// editorial writer takes a COMPLETE set of tags and clears whatever it is not given, so "write
    /// only the genre" can only mean "read this file's tags, lay the new genre over them, write the
    /// result back". The read pays for itself twice: a file the edit does not actually change is
    /// skipped outright, so nothing collects a new timestamp, a re-serialised ID3 tag or a tidied
    /// comment frame over a value it already carried.</para>
    ///
    /// <para>Every write goes through the host's executor, so a file that is PLAYING is deferred
    /// rather than failed, exactly as a single-file save is.</para>
    /// </summary>
    public async Task<TagSaveReport> SaveManyAsync(IProgress<int>? progress = null,
                                                   CancellationToken token = default)
    {
        var plan = BuildPlan();
        if (!plan.HasWork)
        {
            Status = "Nothing to save.";
            IsDirty = false;
            return new TagSaveReport(0, _targets.Count, 0, 0, null);
        }

        var write = _write;
        var targets = _targets;
        var values = Snapshot.From(this);

        var analysis = ParseAnalysis(write, values, out var parseError);
        if (parseError is not null)
        {
            Status = parseError;
            return new TagSaveReport(0, 0, 0, 0, parseError);
        }

        var renames = plan.RenameMask is not null ? PreviewRenames() : [];
        if (renames.Any(r => r.IsProblem))
        {
            Status = "Fix the names the preview flagged first.";
            return new TagSaveReport(0, 0, 0, 0, Status);
        }

        var coverAction = plan.Cover switch
        {
            CoverPlan.Replace => CoverAction.Replace,
            CoverPlan.Remove  => CoverAction.Remove,
            _                 => CoverAction.Keep,
        };
        var coverBytes = coverAction == CoverAction.Replace ? _coverBytes : null;
        var coverMime  = coverAction == CoverAction.Replace ? _coverMime : null;

        int written = 0, unchanged = 0, deferred = 0, failed = 0, done = 0;
        string? firstError = null;

        await Task.Run(() =>
        {
            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var path = targets[i].Path;
                try
                {
                    switch (WriteOne(path, write, values, analysis, coverAction, coverBytes, coverMime))
                    {
                        case null:                     unchanged++; break;
                        case TagWriteOutcome.Deferred: deferred++;  break;
                        case TagWriteOutcome.Written:  written++;   break;
                        default:                       failed++;    break;
                    }

                    if (plan.RenameMask is not null && i < renames.Count && renames[i].To.Length > 0)
                        RenameOne(path, renames[i].To);
                }
                catch (Exception ex)
                {
                    failed++;
                    firstError ??= ex.Message;
                }

                progress?.Report(++done);
            }
        }, token).ConfigureAwait(true);

        Status = failed > 0
            ? $"Saved {written}, {failed} failed."
            : deferred > 0
                ? $"Saved {written} - {deferred} playing, they save at the track change."
                : $"Saved {written}.";

        _baseline = Snapshot.From(this);
        _coverAction = CoverAction.Keep;
        if (CoverState is CoverPlan.Remove or CoverPlan.Replace) CoverState = CoverPlan.Same;
        _uniform |= write;                       // what we just wrote, they now agree on
        IsDirty = false;

        foreach (var t in targets) Saved?.Invoke(t.Path);

        return new TagSaveReport(written, unchanged, deferred, failed, firstError);
    }

    /// <summary>
    /// One file: read what it holds, lay the ticked fields over it, and write only if that actually
    /// changes something. Returns null for "nothing to do".
    /// </summary>
    private TagWriteOutcome? WriteOne(string path, TagField write, Snapshot v, TagWrite? analysis,
                                      CoverAction coverAction, byte[]? coverBytes, string? coverMime)
    {
        var current = _reader.Read(path);

        var next = new EditableTags
        {
            Title       = Pick(write, TagField.Title, v.Title, current.Title),
            Artist      = Pick(write, TagField.Artist, v.Artist, current.Artist),
            Album       = Pick(write, TagField.Album, v.Album, current.Album),
            AlbumArtist = Pick(write, TagField.AlbumArtist, v.AlbumArtist, current.AlbumArtist),
            Genre       = Pick(write, TagField.Genre, v.Genre, current.Genre),
            Comment     = Pick(write, TagField.Comment, v.Comment, current.Comment),
            Year        = PickNum(write, TagField.Year, v.Year, current.Year),
            TrackNumber = PickNum(write, TagField.Track, v.Track, current.TrackNumber),
        };

        var editorialChanged =
            !TextEquals(next.Title, Blank(current.Title)) ||
            !TextEquals(next.Artist, Blank(current.Artist)) ||
            !TextEquals(next.Album, Blank(current.Album)) ||
            !TextEquals(next.AlbumArtist, Blank(current.AlbumArtist)) ||
            !TextEquals(next.Genre, Blank(current.Genre)) ||
            !TextEquals(next.Comment, Blank(current.Comment)) ||
            next.Year != (current.Year ?? 0u) ||
            next.TrackNumber != (current.TrackNumber ?? 0u) ||
            coverAction != CoverAction.Keep;

        var analysisWrite = analysis is null ? null : analysis with
        {
            // The blob records that a HUMAN decided - and that record is per file, so it is built
            // from the file we are standing on, never from whichever one the panel happened to load.
            State = current.StoredAnalysis is { } prev
                ? prev with
                  {
                      BpmDecision    = (write & TagField.Bpm) != 0 ? FieldDecision.Kept : prev.BpmDecision,
                      KeyDecision    = (write & TagField.Key) != 0 ? FieldDecision.Kept : prev.KeyDecision,
                      EnergyDecision = (write & TagField.Energy) != 0 ? FieldDecision.Kept : prev.EnergyDecision,
                  }
                : null,
        };

        if (!editorialChanged && analysisWrite is null) return null;

        return _execute(path, p =>
        {
            if (editorialChanged) _writer.WriteEditable(p, next, coverAction, coverBytes, coverMime);
            if (analysisWrite is not null) _writer.Write(p, analysisWrite);
        });
    }

    private static string? Pick(TagField write, TagField f, string? typed, string? current) =>
        (write & f) != 0 ? Blank(typed) : Blank(current);

    private static uint PickNum(TagField write, TagField f, string? typed, uint? current) =>
        (write & f) != 0 ? ParseUint(typed) : current ?? 0u;

    /// <summary>Parse the three measurements ONCE, before any file is opened - a value we cannot read
    /// has to stop the whole run, not fail on file 12 of 37.</summary>
    private static TagWrite? ParseAnalysis(TagField write, Snapshot v, out string? error)
    {
        error = null;
        if ((write & TagField.Analysis) == 0) return null;

        double? bpm = null;
        MusicalKey? key = null;
        int? energy = null;

        if ((write & TagField.Bpm) != 0 && !string.IsNullOrWhiteSpace(v.Bpm))
        {
            if (!double.TryParse(v.Bpm.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                || b <= 0)
            { error = $"\"{v.Bpm}\" is not a BPM."; return null; }
            bpm = b;
        }

        if ((write & TagField.Key) != 0 && !string.IsNullOrWhiteSpace(v.Key))
        {
            key = MusicalKey.TryParse(v.Key.Trim());
            if (key is null) { error = $"\"{v.Key}\" is not a key we recognise (try Am, F#m, 8A)."; return null; }
        }

        if ((write & TagField.Energy) != 0 && !string.IsNullOrWhiteSpace(v.Energy))
        {
            if (!int.TryParse(v.Energy.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)
                || e is < 1 or > 10)
            { error = "Energy runs from 1 to 10."; return null; }
            energy = e;
        }

        return bpm is null && key is null && energy is null
            ? null
            : new TagWrite { Bpm = bpm, Key = key, Energy = energy };
    }

    private void RenameOne(string path, string newName)
    {
        var to = Path.Combine(Path.GetDirectoryName(path) ?? "", newName);
        if (string.Equals(to, path, StringComparison.OrdinalIgnoreCase)) return;

        File.Move(path, to);
        Renamed?.Invoke(path, to);
    }
}
