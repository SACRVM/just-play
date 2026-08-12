using JustPlay.Core.Abstractions;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// The RAW half of the shared tag editor: every frame/field of the open file exactly as it sits on
/// disk, read-only.
///
/// <para><b>It is a TAB, not a window.</b> The editor already has a tab strip for per-file
/// information - TAGS | ANALYSIS - so a raw-frame view is the third entry in it. A window would have
/// been a second vocabulary for "look at this one file" standing next to the first.</para>
///
/// <para><b>One file, same rule as ANALYSIS.</b> A selection has no single set of frames any more
/// than it has a single BPM, so <see cref="CanShowRaw"/> goes false and the host drops the tab rather
/// than offering an empty one.</para>
///
/// <para><b>Lazy, and only as long as it is wanted.</b> Reading raw containers is a second full open
/// of the file on top of the metadata read every target change already costs, so nothing is read
/// until the tab is actually shown once (<see cref="EnsureRaw"/>). From then on the view keeps up
/// with the file - stepping down a list while standing on RAW has to show the file you are standing
/// on - and a save re-reads, because a save is exactly the thing that changes what is on disk.</para>
/// </summary>
public sealed partial class TagEditorViewModel
{
    /// <summary>Assigned by the constructor in the main partial. Null in a host that has no raw
    /// reader to give, which is what turns the tab off.</summary>
    private readonly IRawTagReader? _rawReader;

    private RawTagsViewModel? _raw;

    /// <summary>The RAW tab has been opened at least once, so it is worth keeping fed. Until then no
    /// file is opened for it at all.</summary>
    private bool _rawWanted;

    /// <summary>
    /// The frames of the file that is open, or null when there is nothing to show one for - no raw
    /// reader, a selection, no file, or the tab has never been opened.
    /// </summary>
    public RawTagsViewModel? Raw
    {
        get => _raw;
        private set { Set(ref _raw, value); Raise(nameof(HasRaw)); }
    }

    /// <summary>
    /// There is a listing to render. The RAW body hangs off THIS and not off <see cref="Raw"/>
    /// directly: an <c>IsVisible</c> bound through a null reference (<c>Raw.HasFailure</c>) produces
    /// no value at all, and a bound property with no value falls back to its default - which for
    /// <c>IsVisible</c> is true. That is how an empty RAW view once rendered "Could not read this
    /// file" over a selection it was never pointed at.
    /// </summary>
    public bool HasRaw => _raw is not null;

    /// <summary>
    /// RAW lists ONE file's containers. Across a selection, or with nothing selected, there is no such
    /// thing - so the hosts hide the tab rather than show an empty one. The same <see cref="IsSingleFile"/>
    /// gate as <see cref="CanShowAnalysis"/>, shared rather than restated: when these two were phrased
    /// separately they disagreed, and the strip showed one per-file tab and not the other.
    /// <para>The extra condition is this editor's own: a host that hands over no reader has no RAW tab
    /// at all (see the constructor).</para>
    /// </summary>
    public bool CanShowRaw => _rawReader is not null && IsSingleFile;

    /// <summary>
    /// The RAW tab is being shown. Reads the file the first time and arms the refresh, so from here
    /// on the listing follows the editor's target instead of freezing on the file it started with.
    /// </summary>
    public void EnsureRaw()
    {
        if (_rawWanted && _raw is not null) return;
        _rawWanted = true;
        RefreshRaw();
    }

    /// <summary>
    /// Re-read the open file's containers, or drop them. Called from every path that changes what the
    /// editor is pointed at and from a save that reached the file.
    /// <para>Costs nothing until the tab has been opened once: without that, there is nothing to keep
    /// up to date and the read is not made.</para>
    /// </summary>
    private void RefreshRaw()
    {
        if (!_rawWanted) { Raw = null; return; }

        Raw = _rawReader is not null && CanShowRaw && FilePath is { } path
            ? new RawTagsViewModel(_rawReader, [path], RawConvertsToLabel)
            : null;
    }

    /// <summary>
    /// The container label a save would convert this file TO, in the shape the raw-tag reader names
    /// its containers by ("ID3v2.3"). <see cref="ConvertsToId3Version"/> carries the short form the
    /// library index uses ("2.3"); <see cref="Id3ConversionNotice.Display"/> is the one place in the
    /// suite that bridges the two shapes, so there is no second version table here to drift.
    /// <para>It reaches the RAW view model only to put the conversion line into the text
    /// <c>Copy all</c> produces. That text leaves the app - into a forum post, an issue, a chat with
    /// an agent - where the editor's own caution card above Save does not travel with it. On SCREEN
    /// the warning is that card, which stands on every tab including this one; RAW deliberately does
    /// not draw a second copy of it a few rows higher.</para>
    /// </summary>
    private string? RawConvertsToLabel =>
        ConvertsToId3Version is { } v ? Id3ConversionNotice.Display(v) : null;
}
