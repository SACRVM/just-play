using Avalonia;
using Avalonia.Controls;
using JustPlay.App.ViewModels;

namespace JustPlay.App.Controls;

/// <summary>
/// The ONE track-list row, shared by the JUST PLAY queue (MaxView) and the Pre-Cue Finder file list. Its
/// DataContext is a <see cref="TrackViewModel"/> (the finder feeds it <c>item.Track</c>); column visibility +
/// sort come from the <see cref="Columns"/> object. Name+artist, key badge, energy/gain/lufs/vibe cells and
/// the like heart all live here once, so the two lists can never drift again (Chloe 2026-07-07). The leading
/// per-view chrome (queue index / UP NEXT vs. the finder's cue + codec badges) stays in each host and docks to
/// the left of this control.
/// </summary>
public partial class TrackRow : UserControl
{
    /// <summary>Shared column visibility + sort state (see <see cref="TrackColumns"/>). The host binds this to
    /// its view-model's Columns instance; every cell's IsVisible reads off it.</summary>
    public static readonly StyledProperty<TrackColumns?> ColumnsProperty =
        AvaloniaProperty.Register<TrackRow, TrackColumns?>(nameof(Columns));

    public TrackColumns? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public TrackRow() => InitializeComponent();
}
