using Avalonia;
using Avalonia.Controls;
using JustPlay.UI.ViewModels;

namespace JustPlay.UI.Controls;

/// <summary>
/// The ONE track-list row, shared by the JUST PLAY queue (MaxView) and the Pre-Cue Finder file list. Its
/// DataContext is a <see cref="TrackViewModel"/> (the finder feeds it <c>item.Track</c>); column visibility +
/// sort come from the <see cref="Columns"/> object. Name+artist, key badge, energy/gain/lufs/vibe cells and
/// the like heart all live here once, so the two lists can never drift again. The leading
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

    // How the artist is shown is NOT a host setting any more (it was ArtistUnderName until
    // 2026-08-05). The row decides: a second line under the title exactly when ARTIST has no column of
    // its own. Two hosts setting the same flag two ways is how the same list ends up showing the artist
    // twice in one app and nowhere in another.

    public TrackRow() => InitializeComponent();

    /// <summary>
    /// A row is as wide as the list, so the row is where the available width is known - and it hands
    /// that to the shared sizing, which re-shares it between the TEXT columns.
    ///
    /// <para>Every row reports the same number, so this is idempotent by design:
    /// <see cref="TrackColumns.FitWidths"/> only publishes widths that actually changed, which means
    /// the second and every later row of a listing do no work. That is deliberately simpler than
    /// wiring a size watcher into three different hosts' list containers - and it cannot be forgotten
    /// by the next host that adopts the row.</para>
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (Columns is { } columns && finalSize.Width > 0)
            columns.FitWidths(finalSize.Width);
        return arranged;
    }
}
