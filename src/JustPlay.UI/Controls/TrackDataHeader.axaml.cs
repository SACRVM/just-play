using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using JustPlay.UI.ViewModels;

namespace JustPlay.UI.Controls;

/// <summary>
/// The ONE column-header strip for the track tables - the JUST PLAY queue (MaxView) and the Pre-Cue Finder
/// file list. Renders the toggleable data-column headers (genre...like) at the SAME shared <c>ColW*</c> widths as
/// <see cref="TrackRow"/>, with the sort glyphs + click-to-sort cycle reading the shared <see cref="Columns"/>
/// (a <see cref="TrackColumns"/>). So header <-> row <-> the other list can never drift on widths, order, alignment
/// or sort state. The letter-spacing flush fix (<see cref="HeaderText"/>) is baked into the label style here, in
/// one place. The host still owns the leading gutter (queue index / finder cue+codec) and the NAME cell, which
/// dock around this control. Chloe 2026-07-08 - the shared-header step of the anti-drift refactor.
/// </summary>
public partial class TrackDataHeader : UserControl
{
    /// <summary>Shared column visibility + sort state; every header cell reads its IsVisible + sort glyph off it,
    /// and a header tap cycles that column's sort through it (asc -> desc -> off).</summary>
    public static readonly StyledProperty<TrackColumns?> ColumnsProperty =
        AvaloniaProperty.Register<TrackDataHeader, TrackColumns?>(nameof(Columns));

    public TrackColumns? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>The DURATION header label - the ONE cell that differs per list: the queue passes its set total
    /// (e.g. "1:23:45"), the finder leaves the default "TIME" label. Kept as a plain string (+ its own
    /// letter-spacing below) rather than an injected control, so there is no content-DataContext ambiguity.</summary>
    public static readonly StyledProperty<string> DurationHeaderProperty =
        AvaloniaProperty.Register<TrackDataHeader, string>(nameof(DurationHeader), "TIME");

    public string DurationHeader
    {
        get => GetValue(DurationHeaderProperty);
        set => SetValue(DurationHeaderProperty, value);
    }

    /// <summary>Letter-spacing for the DURATION header: 2 for the tracked "TIME" label (finder), 0 for the
    /// queue's total number (tight digits). Flush works either way - the compensator derives from this value.</summary>
    public static readonly StyledProperty<double> DurationHeaderLetterSpacingProperty =
        AvaloniaProperty.Register<TrackDataHeader, double>(nameof(DurationHeaderLetterSpacing), 2);

    public double DurationHeaderLetterSpacing
    {
        get => GetValue(DurationHeaderLetterSpacingProperty);
        set => SetValue(DurationHeaderLetterSpacingProperty, value);
    }

    /// <summary>Whether header clicks sort. Default true (the queue). The Pre-Cue Finder binds this to
    /// its "all rows loaded" flag so sorting stays locked until every row's data is in (sort needs it all);
    /// a tooltip on the header explains the wait. Column visibility (right-click) is never gated.</summary>
    public static readonly StyledProperty<bool> CanSortProperty =
        AvaloniaProperty.Register<TrackDataHeader, bool>(nameof(CanSort), defaultValue: true);

    public bool CanSort
    {
        get => GetValue(CanSortProperty);
        set => SetValue(CanSortProperty, value);
    }

    public TrackDataHeader() => InitializeComponent();

    // A header cell was clicked: cycle that column's sort on the shared Columns (which fires SortRequested;
    // each host re-sorts its own rows). The Tag on each cell Border carries the lowercase column id.
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (!CanSort) return; // sorting locked (e.g. finder still loading rows) - the tooltip explains
        if (sender is Control { Tag: string id })
            Columns?.SortBy(id);
    }
}
