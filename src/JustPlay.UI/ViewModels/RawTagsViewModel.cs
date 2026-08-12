using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// One raw tag entry as a row: the HANDLE it is addressed by, what it holds, and whose it is.
///
/// <para>The handle is <c>Id</c> and <c>Descriptor</c> joined with a colon - <c>GEOB:Serato Markers2</c>,
/// <c>PRIV:TRAKTOR4</c>, <c>TXXX:EnergyLevel</c>. That is not a formatting choice: it is the notation
/// the interop dossier, every DJ forum thread and mp3tag itself use, so the row reads as the thing the
/// DJ already has a name for. Xiph and APE keys carry no descriptor and stand alone.</para>
///
/// <para><b>Nothing here is decoded.</b> <see cref="Value"/> is the reader's own summary: the text of a
/// genuinely textual frame, and for a binary one its SIZE. "470 bytes" is a measurement we can stand
/// behind; "Serato cue 3 at 2:14" would be a claim we cannot, because we do not parse those blobs and
/// are not going to.</para>
///
/// <para><b>The vendor is not a column.</b> It was one, sitting between the handle and the value - and
/// ten pixels to the left of a value it reads as the START of that value: a JustPlay frame rendered as
/// "TXXX:JUSTPLAY | JUST PLAY | v9,bpm=148.7,..." and the name looked like part of the blob. It was
/// also redundant exactly where it shouted loudest, because a handle like GEOB:Serato Markers2 or
/// PRIV:TRAKTOR4 already IS the name; it only ever earned its place on a handle that hides its owner
/// (TXXX:EnergyLevel). So the row keeps the COLOUR that separates an owned frame from an ordinary one
/// (Button.rawrow.plain) and the word itself moved one hover away - see <see cref="Tip"/>.</para>
/// </summary>
public sealed record RawTagRow(string Handle, string Value, string Vendor)
{
    public bool HasVendor => Vendor.Length > 0;

    /// <summary>
    /// The row on hover: the full handle, whose it is, the full value, and what a click does.
    ///
    /// <para>ONE tooltip for the whole row rather than one per column. Per-column tooltips made the
    /// answer depend on which child the pointer happened to land on, and the vendor - which belongs to
    /// the row, not to any one of its columns - had nowhere to live in that scheme.</para>
    ///
    /// <para>Newlines in the string are hard breaks: <c>TextLayout.CreateTextLines</c> formats one
    /// TextLine per mandatory break and only stops at TextEndOfParagraph, independently of
    /// TextWrapping (verified against release/12.0.3,
    /// Avalonia.Base/Media/TextFormatting/TextLayout.cs).</para>
    /// </summary>
    public string Tip =>
        Handle
        + (HasVendor ? "\n" + Vendor : string.Empty)
        + "\n" + Value
        + "\n\nClick to copy this line";

    /// <summary>This row as one line of text - what a click on it puts on the clipboard, and what the
    /// whole-file listing is built from. Padded rather than tab-separated so a pasted block still
    /// lines up in a chat window or an issue, neither of which honours tab stops.
    /// <para>The vendor stays INLINE here although the screen shows it only on hover: pasted text has
    /// no pointer, so the bracketed name is the only way the owner survives a copy.</para></summary>
    public string Line =>
        Handle.PadRight(38) + (HasVendor ? $"[{Vendor}]" : string.Empty).PadRight(16) + Value;
}

/// <summary>
/// One <see cref="RawTagContainer"/> as a collapsible section. Collapsible because a fully tagged MP3
/// carries 20-40 frames and the question is usually about one of them; expanded by default because a
/// window whose content starts hidden has to be operated before it says anything.
/// </summary>
public sealed class RawTagSection : INotifyPropertyChanged
{
    public RawTagSection(string label, string countText, string? unsupportedReason,
                         IReadOnlyList<RawTagRow> rows)
    {
        Label = label;
        CountText = countText;
        UnsupportedReason = unsupportedReason;
        Rows = rows;
    }

    /// <summary>The container's own label - "ID3v2.3", "Xiph (Vorbis comments)", "APEv2". Comes
    /// straight from the reader, which derives the ID3 one from the same probe the track table's ID3
    /// column uses, so the two screens cannot disagree about one file's version.</summary>
    public string Label { get; }

    public string CountText { get; }

    /// <summary>Non-null when the reader does not walk this container type in detail. Renders as ONE
    /// greyed explanatory row - an empty table would read as "this file has no APE items", which is a
    /// different and false statement.</summary>
    public string? UnsupportedReason { get; }

    public bool IsUnsupported => UnsupportedReason is not null;
    public bool HasRows => Rows.Count > 0;

    public IReadOnlyList<RawTagRow> Rows { get; }

    private bool _expanded = true;
    public bool IsExpanded
    {
        get => _expanded;
        private set
        {
            if (_expanded == value) return;
            _expanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(IsCollapsed));
        }
    }

    /// <summary>The chevron's rotation is driven off a CLASS, and a class binds to a bool - so the
    /// inverse is a property rather than a converter.</summary>
    public bool IsCollapsed => !_expanded;

    public void Toggle() => IsExpanded = !_expanded;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// RAW TAGS - every frame/field in a file exactly as it sits on disk, read-only, for one selection at
/// a time.
///
/// <para><b>Why it exists.</b> We MEASURED on 2026-07-31 that our writer returns 787 of 787 vendor
/// frame payloads byte-identical - Serato's GEOB set, the 85,166-byte PRIV:TRAKTOR4 blob, Mixed In
/// Key's frames, the POPM user string - where other taggers truncate, rename or wipe them. Nothing in
/// the suite could SHOW that. This window is the showing.</para>
///
/// <para><b>What it must never become.</b> It proves we PRESERVE; it must not pretend we UNDERSTAND.
/// A row says "GEOB:Serato Markers2 - 470 bytes", which is measured and is a strong thing for a DJ to
/// see. It will never say "cue 3 at 2:14", because we do not decode those blobs. Binary entries show
/// their size, never a hexdump and never an interpretation.</para>
///
/// <para><b>Strictly read-only.</b> No edit, no delete, no "strip foreign tags" - not now and not as a
/// stub. Copying a row or the listing is the only thing that leaves this window.</para>
///
/// <para>Reading is per file and LAZY: a selection of 200 files costs one read, not 200. Each file is
/// read once and cached for the session of the window, so stepping back and forth is free. The read is
/// synchronous - it is one file open and a frame walk, and a busy overlay for that would be more
/// machinery than the wait it hides.</para>
///
/// <para>Hand-rolled <see cref="INotifyPropertyChanged"/>, like the rest of the shared UI library.</para>
/// </summary>
public sealed class RawTagsViewModel : INotifyPropertyChanged
{
    private readonly IRawTagReader _reader;
    private readonly string[] _paths;
    private readonly RawTagReadResult?[] _cache;
    private readonly string? _convertsToLabel;

    private int _index;

    /// <param name="convertsToLabel">
    /// The ID3v2 container label a save would convert these files TO -
    /// <c>Id3VersionProbe.Name(Id3VersionProbe.MajorFor(format))</c> in the host - or null when the
    /// configured write format keeps each file's existing version, in which case no conversion can
    /// happen and no notice applies. A LABEL rather than an enum on purpose: the shared UI library
    /// cannot reference JustPlay.Metadata where the probe lives, and comparing the string the probe
    /// produced against the string the reader produced with the same probe is exact by construction.
    /// </param>
    public RawTagsViewModel(IRawTagReader reader, IReadOnlyList<string> paths, string? convertsToLabel = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(paths);

        _reader = reader;
        _paths = [.. paths];
        _cache = new RawTagReadResult?[_paths.Length];
        _convertsToLabel = string.IsNullOrWhiteSpace(convertsToLabel) ? null : convertsToLabel;

        Load();
    }

    // -- The selection ---------------------------------------------------------------------------

    public bool HasMany => _paths.Length > 1;

    /// <summary>"3 / 12" - which file of the selection is on screen. Without it, stepping through a
    /// selection is a walk in the dark.</summary>
    public string PositionText =>
        $"{(_index + 1).ToString(CultureInfo.InvariantCulture)} / {_paths.Length.ToString(CultureInfo.InvariantCulture)}";

    public bool CanPrev => _index > 0;
    public bool CanNext => _index < _paths.Length - 1;

    public void Prev()
    {
        if (!CanPrev) return;
        _index--;
        Load();
    }

    public void Next()
    {
        if (!CanNext) return;
        _index++;
        Load();
    }

    // -- The open file ---------------------------------------------------------------------------

    public string FileName { get; private set; } = string.Empty;

    /// <summary>The folder, not the whole path: the name is already the headline above it, and on a
    /// NAS the interesting half of a path is the folder anyway.</summary>
    public string FolderPath { get; private set; } = string.Empty;

    /// <summary>Set when the file could not be opened or parsed at all - rendered as one honest line
    /// instead of a table. Never an exception: the reader promises that and this window relies on it.</summary>
    public string? FailureReason { get; private set; }

    public bool HasFailure => FailureReason is not null;
    public bool HasContent => FailureReason is null;

    public IReadOnlyList<RawTagSection> Sections { get; private set; } = [];

    /// <summary>"2 containers, 41 entries" - the size of what is below, in one line.</summary>
    public string CountLine { get; private set; } = string.Empty;

    // -- The one honest warning ------------------------------------------------------------------

    /// <summary>
    /// The single residual risk our own measurement found, stated once and calmly.
    ///
    /// <para>An ID3v2 VERSION change re-serialises the whole tag and re-encodes GEOB descriptors. No
    /// vendor byte is lost - the payloads stay identical - but Serato and Mixed In Key look their
    /// frames up BY that description string, so the data can become unfindable while nothing looks
    /// broken. JUST TAG's write-format setting is the only place in the suite that can trigger it,
    /// because it is the only caller of <c>ConfigureId3WriteFormat</c> and those modes set
    /// <c>ForceDefaultVersion = true</c>.</para>
    ///
    /// <para>Gated on BOTH conditions, so it is never a false alarm: the configured write format has to
    /// differ from this file's actual version, AND the file has to carry frames that are addressed by a
    /// descriptor at all (a GEOB belonging to Serato or Mixed In Key). A file with no such frame loses
    /// nothing findable by converting, and telling it otherwise would spend the warning's credibility
    /// on nothing.</para>
    ///
    /// <para>The WORDS are <see cref="Id3ConversionNotice"/>'s, shared with the tag editor's notice
    /// above Save. That editor notice is gated only on the version, because it is about the save you
    /// are one click from making; this one is stricter because it is about a file you are reading.
    /// Different gates, one sentence.</para>
    /// </summary>
    public string? VersionNotice { get; private set; }

    public bool HasVersionNotice => VersionNotice is not null;

    // -- Text out --------------------------------------------------------------------------------

    /// <summary>The whole open file as text - what the Copy button puts on the clipboard. The one
    /// thing that leaves this window.</summary>
    public string ListingText { get; private set; } = string.Empty;

    // -- Building --------------------------------------------------------------------------------

    private void Load()
    {
        var path = _paths.Length > 0 ? _paths[_index] : string.Empty;

        FileName = Path.GetFileName(path);
        FolderPath = Path.GetDirectoryName(path) ?? string.Empty;

        var result = ReadCached(_index);

        FailureReason = result?.FailureReason;
        Sections = result is null ? [] : [.. result.Containers.Select(BuildSection)];
        CountLine = result is null ? string.Empty : BuildCountLine(result);
        VersionNotice = result is null ? null : BuildVersionNotice(result);
        ListingText = BuildListing(path);

        foreach (var name in PerFileProperties) Raise(name);
    }

    /// <summary>Everything on screen belongs to the open file, so a step through the selection
    /// re-raises the lot. Listed once rather than sprinkled through <see cref="Load"/> - a property
    /// that quietly stops refreshing is the classic bug here.</summary>
    private static readonly string[] PerFileProperties =
    [
        nameof(PositionText), nameof(CanPrev), nameof(CanNext),
        nameof(FileName), nameof(FolderPath),
        nameof(FailureReason), nameof(HasFailure), nameof(HasContent),
        nameof(Sections), nameof(CountLine),
        nameof(VersionNotice), nameof(HasVersionNotice),
        nameof(ListingText),
    ];

    private RawTagReadResult? ReadCached(int index)
    {
        if (index < 0 || index >= _paths.Length) return null;
        return _cache[index] ??= _reader.Read(_paths[index]);
    }

    // A sentence summarising who owns what in this file used to be built here - "8 frames from
    // SERATO, 4 from MIXED IN KEY and 2 from JUST PLAY. No TRAKTOR data." It is gone, and with it
    // the list of tools worth naming in prose and the and/or joiners that read it out loud.
    //
    // What killed it was the row tooltip: once every row names its own owner, a summary above the
    // table says the same thing a second time, further from the frame it is about, and it has to
    // invent language the table does not need ("frames from", "no ... data"). One of the two had to
    // go and the row is the one being looked at. VendorName below still exists - it labels the row.

    private static RawTagSection BuildSection(RawTagContainer container)
    {
        // LINQ's OrderBy is a STABLE sort, so file order survives inside each rank - which matters:
        // Serato writes its GEOB set in a fixed order and shuffling it would make two files harder to
        // compare, not easier.
        var rows = container.Entries
            .OrderBy(Rank)
            .Select(e => new RawTagRow(Handle(e), e.Summary, VendorName(e.Vendor)))
            .ToList();

        return new RawTagSection(container.Label, CountText(container), container.UnsupportedReason, rows);
    }

    /// <summary>Another tool's frames first, then the ones we can name without naming a tool (ours and
    /// the ReplayGain standard), then the ordinary text frames. A DJ opens this tab for the eight
    /// GEOBs, not for TIT2 - and the ordinary frames are the ones already shown, decoded, in the
    /// editor next door.</summary>
    private static int Rank(RawTagEntry entry) => entry.Vendor switch
    {
        RawTagVendor.Unknown => 2,
        RawTagVendor.JustPlay or RawTagVendor.ReplayGain => 1,
        _ => 0,
    };

    private static string Handle(RawTagEntry entry) =>
        entry.Descriptor is null ? entry.Id : $"{entry.Id}:{entry.Descriptor}";

    private static string CountText(RawTagContainer container)
    {
        if (container.UnsupportedReason is not null) return string.Empty;

        // ID3v2 has frames; Xiph and APE have fields. Using one word for both would be shorter and
        // would also be the word one of them does not use.
        var noun = container.Kind is RawTagContainerKind.Id3v2 ? "frame" : "field";
        return Plural(container.Entries.Count, noun);
    }

    private static string BuildCountLine(RawTagReadResult result)
    {
        if (result.FailureReason is not null) return string.Empty;
        if (result.Containers.Count == 0) return "No tags in this file.";

        return $"{Plural(result.Containers.Count, "container")}, {Plural(result.TotalEntryCount, "entry", "entries")}";
    }

    private string? BuildVersionNotice(RawTagReadResult result)
    {
        if (_convertsToLabel is null) return null;

        var id3 = result.Containers.FirstOrDefault(c => c.Kind == RawTagContainerKind.Id3v2);
        if (id3 is null || string.Equals(id3.Label, _convertsToLabel, StringComparison.Ordinal)) return null;

        // Only GEOB frames are addressed by their descriptor in a way a re-encode can break, and only
        // Serato's and Mixed In Key's are looked up that way by an app that matters here. Anything
        // else converts without losing a handle, so it gets no warning.
        var addressed = id3.Entries.Any(e =>
            e.Id == "GEOB" && e.Vendor is RawTagVendor.Serato or RawTagVendor.MixedInKey);

        return addressed ? Id3ConversionNotice.Headline(id3.Label, _convertsToLabel) : null;
    }

    private string BuildListing(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FileName);
        sb.AppendLine(path);

        if (FailureReason is { } failure)
        {
            sb.AppendLine($"Could not read this file. {failure}");
            return sb.ToString();
        }

        if (VersionNotice is { } notice)
        {
            sb.AppendLine();
            sb.AppendLine(notice);
            sb.AppendLine(Id3ConversionNotice.Body);
        }

        foreach (var section in Sections)
        {
            sb.AppendLine();
            sb.AppendLine(section.CountText.Length > 0
                ? $"{section.Label} - {section.CountText}"
                : section.Label);

            if (section.UnsupportedReason is { } reason)
            {
                sb.AppendLine($"  {reason}");
                continue;
            }

            foreach (var row in section.Rows)
                sb.AppendLine($"  {row.Line.TrimEnd()}");
        }

        return sb.ToString();
    }

    // -- Small helpers ---------------------------------------------------------------------------

    private static string VendorName(RawTagVendor vendor) => vendor switch
    {
        RawTagVendor.Serato => "SERATO",
        RawTagVendor.Traktor => "TRAKTOR",
        RawTagVendor.MixedInKey => "MIXED IN KEY",
        RawTagVendor.Rekordbox => "REKORDBOX",
        RawTagVendor.ReplayGain => "REPLAYGAIN",
        RawTagVendor.JustPlay => "JUST PLAY",
        _ => string.Empty,
    };

    private static string Plural(int count, string singular, string? plural = null)
    {
        var n = count.ToString(CultureInfo.InvariantCulture);
        return count == 1 ? $"{n} {singular}" : $"{n} {plural ?? singular + "s"}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
