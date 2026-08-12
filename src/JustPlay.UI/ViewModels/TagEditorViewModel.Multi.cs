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
using JustPlay.Core.Tagging;

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
        RenameIssue.NoMatch  => "this name does not fit the pattern - left alone",
        _                    => "",
    };
}

/// <summary>Why a previewed row cannot go ahead. <see cref="None"/> is the good case.</summary>
public enum RenameIssue { None, Collides, Exists, Empty, NoMatch }

/// <summary>
/// What a save is about to do, worked out BEFORE it runs so it can be shown and refused.
/// <para>Writing tags into dozens of files cannot be taken back, so "what is about to happen" has to
/// exist as a value the UI can render - not as a sentence in a code comment.</para>
/// </summary>
public sealed record TagSavePlan(
    int FileCount,
    IReadOnlyList<TagSetting> Sets,
    string? RenameMask,
    CoverPlan Cover,
    string? TagFromNameMask = null,
    IReadOnlyList<string>? TagFromNameFields = null)
{
    public bool ChangesCover => Cover is CoverPlan.Replace or CoverPlan.Remove;

    /// <summary>Would this save touch anything at all?</summary>
    public bool HasWork =>
        Sets.Count > 0 || RenameMask is not null || ChangesCover || TagFromNameMask is not null;
}

/// <summary>One line of the confirmation: this field, set to this value, on every selected file.</summary>
/// <param name="Clears">The value is being emptied - the one setting that can lose data outright.</param>
public sealed record TagSetting(string Field, string Value, bool Clears);

/// <summary>
/// How a multi-file save actually went.
///
/// <para>A RENAME is counted on its own and never folded into <see cref="Failed"/>. The two are
/// different pieces of news: the write puts the edits in the file, the rename changes what it is
/// called, and a file whose tags landed but whose rename did not is a SAVED file with the wrong
/// name - not a failed one. Folding them also counted such a file twice, once in each column, so
/// "12 saved, 1 failed" could come out of 12 files.</para>
/// </summary>
/// <param name="RenamesFailed">Files whose tags landed and whose rename did not.</param>
/// <param name="FirstRenameError">The first rename's reason, for the same "say why" as
/// <paramref name="FirstError"/>.</param>
public sealed record TagSaveReport(int Written, int Unchanged, int Deferred, int Failed,
                                   string? FirstError,
                                   int RenamesFailed = 0, string? FirstRenameError = null);

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
    // needed the same two controls.
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

    /// <summary>
    /// Exactly ONE file is open - the state in which a per-file view has anything to show at all.
    ///
    /// <para>(!) Both per-file tabs hang off this ONE property, and that is the whole point. They used
    /// to phrase the same condition differently - ANALYSIS as <c>!IsMulti</c>, RAW as
    /// <c>!IsMulti &amp;&amp; FilePath is not null</c> - and the two disagree in exactly one state:
    /// nothing selected. The strip then offered ANALYSIS and not RAW, a distinction that does not
    /// exist. Anything else that is per-file goes through here too, rather than re-deriving it.</para>
    /// </summary>
    public bool IsSingleFile => FileCount == 1;

    /// <summary>ANALYSIS is a per-file measurement and has nothing to say about a selection - or about
    /// no selection - so the hosts hide the tab rather than show an empty one.</summary>
    public bool CanShowAnalysis => IsSingleFile;

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

    private bool Ticked(TagField f) => FromName(f) || !IsMulti || (_write & f) != 0;

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
        FromName(f) ? "from the file name"
        : IsMulti && !Ticked(f) && (_uniform & f) == 0 ? what
        : "";

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

    // -- Tags FROM the file name -----------------------------------------------------------------
    //
    // The other direction, and the one that behaves differently from everything else in this panel:
    // it produces a DIFFERENT value per file. The panel's whole model is "one value per field, into
    // every file", so a field the mask fills cannot show a value at all - it shows what it is going
    // to be filled FROM, and goes read-only. Wanting one of those fields by hand means picking a
    // mask that does not name it.

    private string? _fromNameMask;

    /// <summary>The pattern the file NAMES are read apart with, e.g. <c>%artist% - %title%</c>. Null
    /// or unusable means the fields stay yours.</summary>
    public string? TagFromNameMask
    {
        get => _fromNameMask;
        set
        {
            Set(ref _fromNameMask, value);
            _fromName = FieldsOf(value);
            Raise(nameof(TagFromNameLabel));
            Raise(nameof(HasTagFromName));
            Raise(nameof(TagFromNameFit));

            // ONE file: fill the boxes and stop. There is a name to read and one set of fields to
            // put it in, so the result belongs on screen where it can be corrected before it is
            // written - the same way picking a rename pattern only fills the name box. Across a
            // selection there is no single set of fields, so it stays a plan (see FromName).
            if (!IsMulti) { ApplyTagsFromName(); _fromName = TagField.None; }

            RaiseFieldStates();
            MarkDirty();
        }
    }

    /// <summary>Drop the name pattern - see LoadMany for why a change of target always does this.</summary>
    private void ClearTagFromName()
    {
        _fromNameMask = null;
        _fromName = TagField.None;
        Raise(nameof(TagFromNameMask));
        Raise(nameof(TagFromNameLabel));
        Raise(nameof(HasTagFromName));
        Raise(nameof(TagFromNameFit));
        RaiseFieldStates();
    }

    /// <summary>Read THIS file's name apart and put the values in the boxes. Single-file only.</summary>
    private void ApplyTagsFromName()
    {
        if (FilePath is not { } path || !HasTagFromName) return;

        var parsed = FileNameMask.Parse(_fromNameMask!, FileNameMask.SubjectFor(_fromNameMask!, path));
        if (parsed is null || parsed.Count == 0)
        {
            Status = "That name does not fit this pattern.";
            return;
        }

        foreach (var (field, value) in parsed)
            switch (field)
            {
                case "artist":      Artist = value; break;
                case "title":       Title = value; break;
                case "album":       Album = value; break;
                case "albumartist": AlbumArtist = value; break;
                case "genre":       Genre = value; break;
                case "year":        Year = value; break;
                case "track":       Track = value; break;
            }

        Status = $"Filled {parsed.Count} field{(parsed.Count == 1 ? "" : "s")} from the name - not saved yet.";
    }

    /// <summary>The fields the active mask owns.</summary>
    private TagField _fromName = TagField.None;

    public bool HasTagFromName => FileNameMask.CanParse(_fromNameMask);

    public string TagFromNameLabel => HasTagFromName ? _fromNameMask! : "Leave tags alone";

    /// <summary>
    /// How many of the selected names the mask actually fits, counted as you type.
    ///
    /// <para>This is what turns a mask box from a guess into a dial. Without it you write a pattern,
    /// open the preview, close it, change a character, open it again. With it the number moves while
    /// you type and you stop when it stops climbing - which is the difference between five canned
    /// patterns and a language you can aim at your own library.</para>
    /// </summary>
    public string TagFromNameFit
    {
        get
        {
            if (!IsMulti || _targets.Count == 0) return "";
            if (!HasTagFromName)
                return string.IsNullOrWhiteSpace(_fromNameMask) ? "" : "not a usable pattern";

            var mask = _fromNameMask!;
            var fits = _targets.Count(t =>
                FileNameMask.Parse(mask, FileNameMask.SubjectFor(mask, t.Path)) is { Count: > 0 });

            return fits == _targets.Count
                ? $"fits all {fits}"
                : $"fits {fits} of {_targets.Count}";
        }
    }

    /// <summary>
    /// Starting points, offered in the box's drop-down. They are SUGGESTIONS, not the language: the
    /// box takes free text, because five canned patterns cover almost nothing of a real library -
    /// label prefixes, vinyl sides, remix brackets, underscores, and the album or genre sitting in
    /// the folder rather than the name.
    /// </summary>
    public static IReadOnlyList<string> TagMaskSuggestions =>
    [
        "%artist% - %title%",
        "%title% - %artist%",
        "%track% - %artist% - %title%",
        "%dummy% - %artist% - %title%",
        "%artist% - %title% (%album%)",
        "%artist%_%title%",
        "%dummy%. %artist% - %title%",
        "%album%/%artist% - %title%",
        "%genre%/%artist% - %title%",
        "%artist%/%album%/%track% - %title%",
    ];

    /// <summary>The words of the language, shown under the box. One you cannot see the vocabulary of
    /// is one you cannot use.</summary>
    public static string MaskVocabulary =>
        "%artist% %title% %album% %albumartist% %genre% %year% %track%   -   "
        + "%dummy% swallows a part you do not want   -   / reaches into the folder";


    /// <summary>Is this field being filled from the name? Then it is not yours to type in.</summary>
    private bool FromName(TagField f) => HasTagFromName && (_fromName & f) != 0;

    public bool TitleFromName       => FromName(TagField.Title);
    public bool ArtistFromName      => FromName(TagField.Artist);
    public bool AlbumFromName       => FromName(TagField.Album);
    public bool AlbumArtistFromName => FromName(TagField.AlbumArtist);
    public bool GenreFromName       => FromName(TagField.Genre);
    public bool YearFromName        => FromName(TagField.Year);
    public bool TrackFromName       => FromName(TagField.Track);

    /// <summary>Mask field names -> our flags. One place, so a name added to the language reaches
    /// the panel by being listed here and nowhere else.</summary>
    private static TagField FieldsOf(string? mask)
    {
        var f = TagField.None;
        foreach (var name in FileNameMask.FieldsIn(mask))
            f |= name switch
            {
                "artist"      => TagField.Artist,
                "title"       => TagField.Title,
                "album"       => TagField.Album,
                "albumartist" => TagField.AlbumArtist,
                "genre"       => TagField.Genre,
                "year"        => TagField.Year,
                "track"       => TagField.Track,
                _             => TagField.None,
            };
        return f;
    }

    private static string NameOf(TagField f) => f switch
    {
        TagField.Artist      => "artist",
        TagField.Title       => "title",
        TagField.Album       => "album",
        TagField.AlbumArtist => "albumartist",
        TagField.Genre       => "genre",
        TagField.Year        => "year",
        TagField.Track       => "track",
        _                    => "",
    };

    private void RaiseFieldStates()
    {
        foreach (var f in AllFields)
        {
            Raise(HintNameFor(f));
            Raise(WriteNameFor(f));
        }
        Raise(nameof(TitleFromName)); Raise(nameof(ArtistFromName)); Raise(nameof(AlbumFromName));
        Raise(nameof(AlbumArtistFromName)); Raise(nameof(GenreFromName));
        Raise(nameof(YearFromName)); Raise(nameof(TrackFromName));
    }

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

            // The RENAME pattern survives a new selection on purpose - picking one and walking it
            // through folder after folder is the actual workflow. What does not survive is its tick:
            // _write below drops Name, so the pattern is offered, never armed.
            //
            // (!) The TAGS-FROM-NAME pattern is cleared instead, and the difference is not an
            // inconsistency - it is that this one has no tick to disarm. Picking it IS switching it
            // on, so carrying it into a selection you just made would make that selection dirty the
            // instant it appeared, and every click somewhere else would ask about unsaved changes you
            // never made. Nothing that would WRITE survives a change of target.
            ClearTagFromName();
            Raise(nameof(RenameMaskLabel));
            Raise(nameof(HasRenameMask));
            Raise(nameof(RenameTickTip));
            Raise(nameof(RenamePreviewTip));

            DeriveFields(targets.Select(t => t.Tags).ToList());

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

    /// <summary>
    /// Work out every field's shared value (or lack of one) and which ticks start on. Separate from
    /// <see cref="LoadMany"/> because the background probe re-runs it: a host whose rows hydrate
    /// lazily (the PRE CUE FINDER) can hand over a selection whose tags are not all read yet, and
    /// "not read yet" must not be allowed to read as "they differ".
    /// </summary>
    private void DeriveFields(IReadOnlyList<TrackMetadata?> tags)
    {
        // The write-format notice counts across the whole selection, not just a first file - a bulk
        // save converts every MP3 it writes. A file whose tags are not read yet contributes nothing
        // here; the background pass re-runs this the moment it knows, and the count corrects itself.
        SetId3Versions([.. tags.Select(t => t?.Id3Version)]);

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

        // Agreed fields start ticked and show their shared value; disagreeing ones start unticked and
        // empty. NAME and COVER are never ticked by default - neither is something to drift into, and
        // both have their own gesture to opt in with.
        _write = _uniform & ~TagField.Name & ~TagField.Cover;
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
        Raise(nameof(IsSingleFile));
        Raise(nameof(CanShowAnalysis));
        Raise(nameof(CanShowRaw));
        // The RAW listing belongs to whatever file is open now. This runs from every path that
        // changes the target, and only after that path has finished settling FilePath - it is called
        // from the `finally` of each one, and (for the background cover compare) on the UI thread.
        RefreshRaw();
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

        // Does anything still need its TAGS read? A host whose rows hydrate lazily can hand over a
        // selection that is only partly known. When they are all known the pass may stop at the first
        // cover mismatch, which is what it is worth on a big selection; when they are not, every file
        // has to be read anyway - and one read answers both questions.
        var needTags = targets.Any(t => t.Tags is null);

        _ = Task.Run(() =>
        {
            var tags = new TrackMetadata?[targets.Count];
            byte[]? shared = null;
            string? sharedHash = null;
            var same = true;

            for (var i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) return;

                TrackMetadata? m;
                try { m = _reader.Read(targets[i].Path); }
                catch { m = null; same = false; if (!needTags) break; else continue; }

                tags[i] = m;
                var bytes = m.CoverArt;

                if (i == 0) { shared = bytes; sharedHash = Hash(bytes); continue; }
                if (!same) continue;

                if ((bytes?.Length ?? 0) != (shared?.Length ?? 0) ||
                    !string.Equals(Hash(bytes), sharedHash, StringComparison.Ordinal))
                {
                    same = false;
                    if (!needTags) break;
                }
            }

            if (token.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || CoverState != CoverPlan.Comparing) return;

                // Fill in what the rows could not tell us. Only while the panel is still CLEAN: once
                // there are edits in it, re-deriving would overwrite what was typed - and the answer
                // is worth less than the typing.
                if (needTags && !IsDirty)
                {
                    _loading = true;
                    try { DeriveFields(tags); _baseline = Snapshot.From(this); }
                    finally { _loading = false; }
                    RaiseMultiState();
                }

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

    // -- Transforming what is already there ------------------------------------------------------

    /// <summary>
    /// A TRANSFORM over <paramref name="targets"/> - the other kind of bulk edit. This panel sets a
    /// field to ONE value across a selection; a transform changes the value that is already in each
    /// file ("PERC - GOB" -> "Perc - Gob", "_" -> " ", a site name out of the comment).
    ///
    /// <para>Built HERE rather than by the host so it inherits this editor's reader, writer and
    /// executor. The executor is the point: a write must reach the file by exactly the route a save
    /// does, or a file that is playing would be deferred by one window and fail in the other.</para>
    /// </summary>
    public TagTransformViewModel Transform(IReadOnlyList<TagTarget> targets) =>
        new(_reader, _writer, _execute, targets);

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
            // Filled per file from the name - it has no ONE value, and the plan says so on its own
            // line instead of pretending to.
            if (FromName(f)) return;
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

        return new TagSavePlan(FileCount, sets, mask, cover,
                               HasTagFromName ? _fromNameMask : null,
                               HasTagFromName ? FileNameMask.FieldsIn(_fromNameMask) : null);
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

    /// <summary>
    /// What reading the names apart would produce, per file - the same look-before-you-leap the
    /// rename preview is, in the other direction. A name that does not fit the mask is shown as
    /// exactly that: left alone. It is not an error, it is the answer.
    /// <para>Costs nothing - the names are already in hand.</para>
    /// </summary>
    public IReadOnlyList<RenamePreviewRow> PreviewTagsFromName()
    {
        if (!HasTagFromName || _targets.Count == 0) return [];

        var mask = _fromNameMask!;
        var rows = new List<RenamePreviewRow>(_targets.Count);

        foreach (var t in _targets)
        {
            var from = Path.GetFileName(t.Path);
            var parsed = FileNameMask.Parse(mask, FileNameMask.SubjectFor(mask, t.Path));

            if (parsed is null || parsed.Count == 0)
            {
                rows.Add(new RenamePreviewRow(from, "", RenameIssue.NoMatch));
                continue;
            }

            // field = value, in the order the mask names them, so the line reads like the pattern.
            var shown = string.Join("   ",
                FileNameMask.FieldsIn(mask)
                            .Where(parsed.ContainsKey)
                            .Select(f => $"{f} = {parsed[f]}"));

            rows.Add(new RenamePreviewRow(from, shown, RenameIssue.None));
        }

        return rows;
    }

    /// <summary>The mask resolved against ONE file's tags. Goes through the SHARED
    /// <see cref="FileNameMask"/> - the same language that reads names apart also writes them, so a
    /// placeholder cannot come to mean two things.</summary>
    private static string Resolve(string mask, TrackMetadata? m) =>
        FileNameMask.Format(mask, field => field switch
        {
            "artist"      => m?.Artist,
            "title"       => m?.Title,
            "album"       => m?.Album,
            "albumartist" => m?.AlbumArtist,
            "genre"       => m?.Genre,
            "year"        => Num(m?.Year),
            "track"       => Num(m?.TrackNumber),
            _             => null,
        });

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
        int renamesFailed = 0, renamesHeld = 0;
        string? firstError = null;
        string? firstRenameError = null;

        // Which files a write actually reached, per index - what the ID3-version notice needs to know
        // afterwards. A skipped or failed file was NOT converted, and must keep its warning.
        var didWrite = new bool[targets.Count];

        await Task.Run(() =>
        {
            for (var i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var path = targets[i].Path;

                // What THIS file's write did, kept per item - the counters above are running totals
                // and cannot answer "why was this one's rename not attempted?".
                //
                // The single-file path makes the same two calls for the same reasons: a write that
                // FAILED must not also change the file's name (the edits are not in it), and a write
                // that was DEFERRED cannot be renamed at all - the queued write is aimed at the old
                // path and the engine is holding the handle.
                var wroteOk = true;
                var held = false;

                try
                {
                    switch (WriteOne(path, write, values, analysis, coverAction, coverBytes,
                                     coverMime, plan.TagFromNameMask))
                    {
                        case null:                     unchanged++; break;
                        case TagWriteOutcome.Deferred: deferred++;  wroteOk = false; held = true; break;
                        case TagWriteOutcome.Written:  written++; didWrite[i] = true; break;
                        default:                       failed++;    wroteOk = false; break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    wroteOk = false;
                    firstError ??= ex.Message;
                }

                // Its OWN try and its own counters - see TagSaveReport for why a rename is not a
                // write and must not be reported as one.
                if (plan.RenameMask is not null && i < renames.Count && renames[i].To.Length > 0)
                {
                    if (held) renamesHeld++;
                    else if (wroteOk)
                    {
                        try
                        {
                            RenameOne(path, renames[i].To);
                        }
                        catch (Exception ex)
                        {
                            renamesFailed++;
                            firstRenameError ??= ex.Message;
                        }
                    }
                }

                progress?.Report(++done);
            }
        }, token).ConfigureAwait(true);

        Status = SaveStatus(written, deferred, failed, renamesHeld, renamesFailed);

        _baseline = Snapshot.From(this);
        _coverAction = CoverAction.Keep;
        if (CoverState is CoverPlan.Remove or CoverPlan.Replace) CoverState = CoverPlan.Same;
        _uniform |= write;                       // what we just wrote, they now agree on
        IsDirty = false;
        MarkId3Converted(didWrite);              // the files we wrote now carry the write format's version

        foreach (var t in targets) Saved?.Invoke(t.Path);

        return new TagSaveReport(written, unchanged, deferred, failed, firstError,
                                 renamesFailed, firstRenameError);
    }

    /// <summary>
    /// The one line a finished batch leaves on screen. Every outcome that happened is named and none
    /// of them hides another: a rename that did not happen is said next to the saves that did, never
    /// instead of them.
    ///
    /// <para>Pure and public so a test can assert on the WORDS - the same reason
    /// <c>AnalysisRunText.Summarise</c> and <c>OrganiseText</c> are their own callable things. A
    /// sentence built inline in a save loop is one that can only be checked from a screenshot, and
    /// this one carries the part that must never be lost.</para>
    /// </summary>
    public static string SaveStatus(int written, int deferred, int failed,
                                    int renamesHeld, int renamesFailed)
    {
        var parts = new List<string>(4) { $"Saved {written}" };

        if (deferred > 0) parts.Add($"{deferred} playing, they save at the track change");
        if (failed > 0) parts.Add($"{failed} failed");

        // "the tags did land" is the half that would otherwise be lost - the same sentence the
        // single-file path says as "Tags saved, rename failed".
        if (renamesFailed > 0)
            parts.Add(renamesFailed == 1
                ? "1 rename failed, its tags did land"
                : $"{renamesFailed} renames failed, their tags did land");

        if (renamesHeld > 0)
            parts.Add(renamesHeld == 1
                ? "1 rename needs the track stopped"
                : $"{renamesHeld} renames need the track stopped");

        return string.Join(" - ", parts) + ".";
    }

    /// <summary>
    /// One file: read what it holds, lay the ticked fields over it, and write only if that actually
    /// changes something. Returns null for "nothing to do".
    /// </summary>
    private TagWriteOutcome? WriteOne(string path, TagField write, Snapshot v, TagWrite? analysis,
                                      CoverAction coverAction, byte[]? coverBytes, string? coverMime,
                                      string? fromName)
    {
        var current = _reader.Read(path);

        // Tags read OUT of this file's own name. Null when there is no mask, or when this name does
        // not fit it - and "does not fit" means the file is left exactly as it was rather than being
        // given whatever a loose pattern happened to capture.
        var parsed = fromName is null
            ? null
            : FileNameMask.Parse(fromName, FileNameMask.SubjectFor(fromName, path));

        string? Field(TagField f, string? typed, string? currently) =>
            parsed is not null && parsed.TryGetValue(NameOf(f), out var fromFile)
                ? fromFile
                : Pick(write, f, typed, currently);

        var next = new EditableTags
        {
            Title       = Field(TagField.Title, v.Title, current.Title),
            Artist      = Field(TagField.Artist, v.Artist, current.Artist),
            Album       = Field(TagField.Album, v.Album, current.Album),
            AlbumArtist = Field(TagField.AlbumArtist, v.AlbumArtist, current.AlbumArtist),
            Genre       = Field(TagField.Genre, v.Genre, current.Genre),
            Comment     = Pick(write, TagField.Comment, v.Comment, current.Comment),
            Year        = ParseUint(Field(TagField.Year, v.Year, Num(current.Year)))
                          is var y && y > 0 ? y : PickNum(write, TagField.Year, v.Year, current.Year),
            TrackNumber = ParseUint(Field(TagField.Track, v.Track, Num(current.TrackNumber)))
                          is var t && t > 0 ? t : PickNum(write, TagField.Track, v.Track, current.TrackNumber),
        };

        // The "did this actually change anything" comparison is the SHARED one - see EditorialWrite,
        // which the TRANSFORM window writes through as well. The cover is this method's own business:
        // it is not an editorial field, and it rides along in the same call.
        var editorialChanged = EditorialWrite.Changes(next, current) || coverAction != CoverAction.Keep;

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

    /// <summary>
    /// Rename one file of a batch. Runs on a WORKER thread - the batch save is inside a
    /// <c>Task.Run</c> - which is why the event does not go out from here directly.
    ///
    /// <para>(!!) <see cref="Renamed"/> is raised on the UI THREAD, always, from both the single-file
    /// path and this one. A host handler moves rows in a bound collection and touches bound state; the
    /// one in JUST PLAY already had to post half of itself to the dispatcher, which is the shape of a
    /// contract that was not being kept. One thread for the event, decided here, is cheaper than every
    /// host having to know which of the two paths it was called from.</para>
    /// </summary>
    private void RenameOne(string path, string newName)
    {
        var to = Path.Combine(Path.GetDirectoryName(path) ?? "", newName);

        // (!!) Ordinal, NOT OrdinalIgnoreCase. The ignore-case form skipped a rename that only
        // changes capitalisation - "track.mp3" -> "Track.mp3" did nothing and reported success -
        // and on a library being tidied up that is the most common rename there is.
        //
        // The filesystem was never the obstacle: File.Move over a case-only difference is a plain
        // rename in the same directory and succeeds on NTFS (measured), on a case-sensitive
        // filesystem it is an ordinary rename, and on a case-insensitive one it is how Finder and
        // Explorer do it too. Only an EXACTLY equal name is nothing to do. The preview agrees - it
        // compares case-insensitively when looking for a name that is already TAKEN, so a file
        // renaming itself is not flagged as colliding with itself.
        if (string.Equals(to, path, StringComparison.Ordinal)) return;

        File.Move(path, to);

        if (Renamed is { } handler) Dispatcher.UIThread.Post(() => handler(path, to));
    }
}
