using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Tagging;

namespace JustPlay.UI.ViewModels;

/// <summary>One operation in the picker: what it is called, and what it does.</summary>
public sealed record OperationChoice(string Label, TextOperation Value);

/// <summary>
/// One line of the transform preview: this file's THIS field, as it reads now and as it would read.
/// <para>A line per FIELD rather than per file, because one pass can touch the artist and the title
/// of the same file and folding those into one row would hide half of what is about to happen.</para>
/// </summary>
public sealed record TextChangeRow(string File, string Field, string Before, string After)
{
    /// <summary>The transform EMPTIES this field. The one outcome here that loses a value instead of
    /// changing it, so it is called out rather than shown as a blank cell.</summary>
    public bool Clears => After.Length == 0;
}

/// <summary>
/// TRANSFORM - the other kind of bulk edit.
///
/// <para>The multi-file editor sets a field to ONE value across a selection. This changes the value
/// that is ALREADY there, per file: fix the shouting ("PERC - GOB" -> "Perc - Gob"), swap the
/// underscores for spaces, take a site name out of every comment. It is the first thing anybody does
/// to a folder that just finished downloading, and it is mp3tag's most-used feature.</para>
///
/// <para><b>Nothing happens until Apply, and Apply cannot be reached without the preview.</b> The
/// list below the controls is not a courtesy - it is the whole safety model, because there is no undo
/// in this repo by design. Files that would not change are not listed at all, and the count line says
/// so, so "37 selected, 14 change" is one sentence rather than something to count.</para>
///
/// <para>Hand-rolled <see cref="INotifyPropertyChanged"/>, like the rest of the shared UI library -
/// no MVVM toolkit down here.</para>
/// </summary>
public sealed class TagTransformViewModel : INotifyPropertyChanged
{
    private readonly IMetadataReader _reader;
    private readonly IMetadataWriter _writer;
    private readonly TagWriteExecutor _execute;

    private readonly List<TagTarget> _targets;

    public TagTransformViewModel(IMetadataReader reader, IMetadataWriter writer,
                                 TagWriteExecutor execute, IReadOnlyList<TagTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        _reader = reader;
        _writer = writer;
        _execute = execute;
        _targets = [.. targets];

        _operation = Operations[0];
        _primed = !NeedsRead;
        Rebuild();
    }

    /// <summary>How many files the transform is pointed at.</summary>
    public int FileCount => _targets.Count;

    /// <summary>Every file this would touch - what the host re-reads once a run is over.</summary>
    public IReadOnlyList<string> Paths => _targets.Select(t => t.Path).ToList();

    // ============================================================================================
    // WHICH FIELDS
    //
    // One flag set rather than six bools, for the reason the editor's ticks use one: a transform has
    // to state WHICH FIELDS, and a mask is that sentence.
    //
    // ARTIST and TITLE start on. They are the two fields a freshly downloaded folder gets wrong
    // almost every time, and nothing is written until Apply - so a default that saves two clicks in
    // the common case costs nothing in the uncommon one.
    // ============================================================================================

    private TagField _fields = TagField.Artist | TagField.Title;

    private bool On(TagField f) => (_fields & f) != 0;

    private void Turn(TagField f, bool on, [CallerMemberName] string? name = null)
    {
        var next = on ? _fields | f : _fields & ~f;
        if (next == _fields) return;

        _fields = next;
        Raise(name);
        Rebuild();
    }

    public bool DoArtist      { get => On(TagField.Artist);      set => Turn(TagField.Artist, value); }
    public bool DoTitle       { get => On(TagField.Title);       set => Turn(TagField.Title, value); }
    public bool DoAlbum       { get => On(TagField.Album);       set => Turn(TagField.Album, value); }
    public bool DoAlbumArtist { get => On(TagField.AlbumArtist); set => Turn(TagField.AlbumArtist, value); }
    public bool DoGenre       { get => On(TagField.Genre);       set => Turn(TagField.Genre, value); }
    public bool DoComment     { get => On(TagField.Comment);     set => Turn(TagField.Comment, value); }

    private bool HasFields => (_fields & TagField.Editorial) != 0;

    // ============================================================================================
    // THE OPERATION
    // ============================================================================================

    /// <summary>
    /// What the picker offers. The three case entries are SPELLED the way they behave, which is the
    /// shortest possible description of what each one does.
    /// </summary>
    public IReadOnlyList<OperationChoice> Operations { get; } =
    [
        new("Replace text", TextOperation.Replace),
        new("Title Case", TextOperation.TitleCase),
        new("Sentence case", TextOperation.SentenceCase),
        new("lowercase", TextOperation.Lowercase),
        new("UPPERCASE", TextOperation.Uppercase),
        new("Tidy spacing", TextOperation.Tidy),
    ];

    private OperationChoice _operation;

    public OperationChoice Operation
    {
        get => _operation;
        set
        {
            // The picker can hand back null while its items are being swapped - that is not a choice,
            // and taking it would leave the window with no operation at all.
            if (value is null || ReferenceEquals(value, _operation)) return;
            _operation = value;
            Raise();
            Raise(nameof(IsReplace));
            Raise(nameof(Explanation));
            Rebuild();
        }
    }

    /// <summary>The find/replace boxes belong to exactly one operation, so they are only there for
    /// it - an input that does nothing is worse than no input.</summary>
    public bool IsReplace => _operation.Value == TextOperation.Replace;

    private string? _find;
    public string? Find
    {
        get => _find;
        set { if (_find == value) return; _find = value; Raise(); Rebuild(); }
    }

    private string? _with;
    public string? ReplaceWith
    {
        get => _with;
        set { if (_with == value) return; _with = value; Raise(); Rebuild(); }
    }

    private bool _matchCase;
    public bool MatchCase
    {
        get => _matchCase;
        set { if (_matchCase == value) return; _matchCase = value; Raise(); Rebuild(); }
    }

    /// <summary>
    /// One line under the picker saying what this operation does to a word you did not think about.
    /// Title Case gets the longest one because it is the operation whose result people guess wrong.
    /// </summary>
    public string Explanation => _operation.Value switch
    {
        TextOperation.Replace =>
            "Every occurrence, everywhere in the field. An empty replacement takes the text out.",
        TextOperation.TitleCase =>
            "Every word gets a capital - after a space, a dash or a bracket. Nothing is kept: "
            + "DJ becomes Dj and VIP becomes Vip. Put one back with a second pass of Replace.",
        TextOperation.SentenceCase =>
            "One capital, on the first letter. Nothing after a full stop - a tag is not prose.",
        TextOperation.Lowercase  => "Every letter small.",
        TextOperation.Uppercase  => "EVERY LETTER BIG.",
        TextOperation.Tidy =>
            "Double spaces, tabs and the invisible spaces a web page leaves behind all become one "
            + "plain space.",
        _ => "",
    };

    // ============================================================================================
    // THE PREVIEW
    // ============================================================================================

    private IReadOnlyList<TextChangeRow> _rows = [];

    /// <summary>Every field that would change, one row each. Files that change nothing are absent -
    /// see <see cref="Summary"/>, which says so in words.</summary>
    public IReadOnlyList<TextChangeRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    private string _summary = "";

    /// <summary>The one sentence that decides whether you press Apply or go back to the boxes.</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>Nothing would change, so there is nothing to look at - the list says so instead of
    /// being an empty box.</summary>
    public bool IsEmpty => _rows.Count == 0;

    private bool _busy;

    /// <summary>Reading the tags, or writing them. The window's overlay hangs off this, and so does
    /// Apply - a second press while the first is still running would write everything twice.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set { Set(ref _busy, value); Raise(nameof(CanApply)); }
    }

    private string? _busyText;
    public string? BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    private string? _busyDetail;

    /// <summary>The counter inside the overlay's ring - "14 / 37".</summary>
    public string? BusyDetail { get => _busyDetail; private set => Set(ref _busyDetail, value); }

    private double? _busyProgress;

    /// <summary>How far a write has got, 0..1. Null while READING, where the length is known but the
    /// per-file cost is not - the ring travels instead of filling, which is the honest picture.</summary>
    public double? BusyProgress { get => _busyProgress; private set => Set(ref _busyProgress, value); }

    /// <summary>
    /// Apply is live only when the preview has something in it AND every file's tags are in hand.
    /// That is the whole rule: what you can start is exactly what you were shown - so a selection
    /// still being read cannot be applied to on the strength of a half-built list.
    /// </summary>
    public bool CanApply => _rows.Count > 0 && !_busy && _primed;

    /// <summary>Every file's tags have been read at least once, so the preview is complete.</summary>
    private bool _primed;

    private void MarkPrimed()
    {
        if (_primed) return;
        _primed = true;
        Raise(nameof(CanApply));
    }

    /// <summary>
    /// Work out every change, from scratch. Cheap by construction - it is string work over tags that
    /// are already in hand, so it can run on every keystroke and the count moves while you type.
    /// </summary>
    private void Rebuild()
    {
        if (!HasFields)
        {
            Show([], "Pick at least one field.");
            return;
        }

        if (!TextTransform.IsUsable(_operation.Value, _find))
        {
            Show([], "Type what to find.");
            return;
        }

        var rows = new List<TextChangeRow>();
        var files = 0;

        foreach (var t in _targets)
        {
            if (t.Tags is not { } tags) continue;   // not read yet - see PrimeAsync

            var name = Path.GetFileName(t.Path);
            var touched = false;

            foreach (var f in EditorialWrite.TextFields)
            {
                if (!On(f)) continue;

                var before = EditorialWrite.Value(tags, f);
                if (before is null) continue;       // an empty field has nothing to transform

                var after = EditorialWrite.Blank(Transform(before));
                if (EditorialWrite.TextEquals(before, after)) continue;

                rows.Add(new TextChangeRow(name, EditorialWrite.Label(f), before, after ?? ""));
                touched = true;
            }

            if (touched) files++;
        }

        // The list is not virtualised (nor is the rename preview it sits beside), and a selection can
        // be the whole of a library root. Past this many lines nobody is reading them one by one
        // anyway - the COUNT is what is being read, and that stays exact.
        Show(rows.Count > MaxRows ? rows.GetRange(0, MaxRows) : rows, Summarise(rows, files));
    }

    /// <summary>How many preview lines are drawn at most - see <see cref="Rebuild"/>.</summary>
    private const int MaxRows = 500;

    private void Show(IReadOnlyList<TextChangeRow> rows, string summary)
    {
        Rows = rows;
        Summary = summary;
        Raise(nameof(IsEmpty));
        Raise(nameof(CanApply));
    }

    private string Summarise(IReadOnlyList<TextChangeRow> rows, int files)
    {
        var all = _targets.Count;

        // Still reading: "nothing changes" would be a lie about files nobody has looked at yet.
        if (!_primed)
            return rows.Count == 0
                ? "Reading tags..."
                : $"Reading tags... {files} of {all} files change so far.";

        if (rows.Count == 0)
            return all == 1
                ? "Nothing changes - this file already reads that way."
                : $"Nothing changes - all {all} files already read that way.";

        var fields = rows.Count == 1 ? "1 field" : $"{rows.Count} fields";
        var line = $"Changes {files} of {all} files ({fields}). "
                   + "Files that do not change are not listed - nothing happens to them.";

        var cleared = rows.Count(r => r.Clears);
        if (cleared > 0)
            line += cleared == 1
                ? "  One of them is EMPTIED."
                : $"  {cleared} of them are EMPTIED.";

        if (rows.Count > MaxRows) line += $"  Listing the first {MaxRows}.";

        return line;
    }

    /// <summary>The transform itself, in one place - the preview and the write must never be able to
    /// compute a different answer.</summary>
    private string? Transform(string? value) =>
        TextTransform.Apply(_operation.Value, value, _find, _with, _matchCase);

    /// <summary>What one field of one file should become. Handed to
    /// <see cref="EditorialWrite.One"/>, which asks it for every editorial field; an unticked one
    /// gets its own value back, which is how "leave it alone" is said.</summary>
    private string? ValueFor(TagField field, string? current) =>
        On(field) ? Transform(current) : current;

    // ============================================================================================
    // READING WHAT IS MISSING
    // ============================================================================================

    /// <summary>Some rows were handed over before their tags had been read - those files cannot be
    /// previewed until they are.</summary>
    public bool NeedsRead => _targets.Exists(t => t.Tags is null);

    /// <summary>
    /// Fill in the tags the host could not supply. A host whose rows hydrate lazily (JUST TAG on a
    /// folder you just opened) can hand over a selection that is only partly known, and "not read
    /// yet" must never quietly read as "nothing changes here".
    /// </summary>
    public async Task PrimeAsync(CancellationToken token = default)
    {
        if (!NeedsRead) { MarkPrimed(); return; }

        IsBusy = true;
        BusyText = "Reading tags...";
        BusyDetail = null;
        BusyProgress = null;

        try
        {
            var read = await Task.Run(() =>
            {
                var found = new Dictionary<string, TrackMetadata>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in _targets)
                {
                    token.ThrowIfCancellationRequested();
                    if (t.Tags is not null || found.ContainsKey(t.Path)) continue;

                    // An unreadable file is left without tags rather than failing the whole window:
                    // it simply has nothing to preview, and the write would fail on it anyway.
                    try { found[t.Path] = _reader.Read(t.Path); }
                    catch (Exception) { }
                }
                return found;
            }, token).ConfigureAwait(true);

            for (var i = 0; i < _targets.Count; i++)
                if (_targets[i].Tags is null && read.TryGetValue(_targets[i].Path, out var m))
                    _targets[i] = _targets[i] with { Tags = m };
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }

        // Primed even when a file could not be opened: it has nothing to preview and the write would
        // fail on it anyway, and leaving the flag off would disable Apply for the whole selection
        // because of one broken file.
        MarkPrimed();
        Raise(nameof(NeedsRead));
        Rebuild();
    }

    // ============================================================================================
    // THE WRITE
    // ============================================================================================

    /// <summary>
    /// Run the transform over every selected file.
    ///
    /// <para>Straight through the shared <see cref="EditorialWrite.One"/>: read the file, lay the
    /// transformed values over what it holds, write only if that changes something. A file the
    /// transform leaves alone is not touched at all - no new timestamp, no re-serialised tag.</para>
    ///
    /// <para>(!) The transform runs against what the FILE says at write time, not against the
    /// snapshot the preview was built from. The file is the truth, and it is being read anyway.</para>
    /// </summary>
    public async Task<TagSaveReport> ApplyAsync(CancellationToken token = default)
    {
        if (!CanApply) return new TagSaveReport(0, _targets.Count, 0, 0, null);

        var targets = _targets.ToList();

        IsBusy = true;
        BusyText = "Writing tags...";
        BusyDetail = $"0 / {targets.Count}";
        BusyProgress = 0;

        // Progress<T> posts back to the context it was BUILT on - the UI thread, since Apply is a
        // button - so the loop below can report from a worker without a dispatcher call of its own.
        var progress = new Progress<int>(n =>
        {
            BusyDetail = $"{n} / {targets.Count}";
            BusyProgress = targets.Count > 0 ? (double)n / targets.Count : null;
        });

        int written = 0, unchanged = 0, deferred = 0, failed = 0, done = 0;
        string? firstError = null;

        try
        {
            await Task.Run(() =>
            {
                foreach (var t in targets)
                {
                    token.ThrowIfCancellationRequested();

                    try
                    {
                        switch (EditorialWrite.One(t.Path, _reader, _writer, _execute, ValueFor))
                        {
                            case null:                     unchanged++; break;
                            case TagWriteOutcome.Deferred: deferred++;  break;
                            case TagWriteOutcome.Written:  written++;   break;
                            default:                       failed++;    break;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        firstError ??= ex.Message;
                    }

                    ((IProgress<int>)progress).Report(++done);
                }
            }, token).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            BusyDetail = null;
            BusyProgress = null;
        }

        return new TagSaveReport(written, unchanged, deferred, failed, firstError);
    }

    /// <summary>
    /// Throw the held tags away and read them again. Used when a run did NOT go cleanly and the
    /// window therefore stays open: a preview built on what the files said BEFORE the write would be
    /// offering to make changes that have already happened.
    /// </summary>
    public Task RefreshAsync(CancellationToken token = default)
    {
        for (var i = 0; i < _targets.Count; i++) _targets[i] = _targets[i] with { Tags = null };
        _primed = false;
        Raise(nameof(NeedsRead));
        Raise(nameof(CanApply));
        return PrimeAsync(token);
    }

    /// <summary>Say what a finished run did, in the summary line - the window stays open when
    /// anything went wrong, and the reason has to be on screen rather than in a log.</summary>
    public void Report(TagSaveReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Summary = report.Failed > 0
            ? $"Wrote {report.Written}, {report.Failed} failed"
              + (report.FirstError is { Length: > 0 } e ? $" - {e}" : ".")
            : report.Deferred > 0
                ? $"Wrote {report.Written} - {report.Deferred} playing, they save at the track change."
                : $"Wrote {report.Written}.";
    }

    // -- INotifyPropertyChanged ------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
