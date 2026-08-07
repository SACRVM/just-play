using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JustPlay.Core.Layout;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// The width of every FLEXIBLE text column in one track table, sized to what that table is actually
/// showing.
///
/// <para><b>The idea</b> (Chloe 2026-08-06): a column should be as wide as its CONTENT needs, not as
/// wide as someone once typed into a resource dictionary. Measure the 90th percentile of each
/// column's character count over the rows currently listed - 90th, so the one track with the
/// 140-character title cannot dictate the whole table - then share the space left over after the
/// fixed columns out in proportion to those numbers. The arithmetic is
/// <see cref="ColumnWidthFitter"/>, which is platform-agnostic and unit-tested; this class is the
/// part that knows about rows and columns.</para>
///
/// <para><b>Per view, deliberately.</b> One instance belongs to one table (the queue, the finder,
/// JUST TAG each own a <see cref="TrackColumns"/>, and therefore one of these), and it re-measures
/// on every listing. That was a real decision, not a default: measured on her library
/// (night task C1, 2026-08-07) the P90 of a column moves by <b>50-130 %</b> of its global value from
/// one folder to the next. Sizing globally would be wrong nearly everywhere.</para>
/// </summary>
public sealed partial class TrackColumnWidths : ObservableObject
{
    /// <summary>
    /// Floors, in px - MEASURED, not chosen (night task C1, 2026-08-07, over 18,009 rows).
    ///
    /// <para>The first set was picked by hand (150 for the title, 60 for everything else) and every
    /// one of them was too small; 150 sits below the title column's own MEDIAN. These are the
    /// measured replacements. A floor is what the column gets when there is not enough room to be
    /// proportional - it is a readability guarantee, not a preferred width.</para>
    ///
    /// <para><see cref="TrackColumns.AlbumArtist"/> keeps the small floor on purpose: it is empty in
    /// 58 % of her library and up to 100 % of a given folder, and the fitter already gives an empty
    /// column its floor and nothing more.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> Floors =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            // "never lose the title" (Chloe 2026-07-08) - and 170 is what her real titles need.
            [TrackColumns.Title]       = 170,
            [TrackColumns.Artist]      = 85,
            [TrackColumns.Album]       = 90,
            [TrackColumns.AlbumArtist] = 60,
            [TrackColumns.Genre]       = 75,
            [TrackColumns.Comment]     = 95,
            [TrackColumns.FileName]    = 100,
        };

    /// <summary>The columns this sizes, in the order the row draws them. Everything else in the table
    /// is a number or a mark and keeps its fixed <c>ColW*</c> width.</summary>
    public static readonly IReadOnlyList<string> Flexible =
    [
        TrackColumns.Title, TrackColumns.Artist, TrackColumns.Album, TrackColumns.AlbumArtist,
        TrackColumns.Genre, TrackColumns.Comment, TrackColumns.FileName,
    ];

    private readonly Dictionary<string, double> _weights = new(StringComparer.Ordinal);
    private double _available;
    private bool _remeasured;

    [ObservableProperty] private double _artist      = 150;
    [ObservableProperty] private double _album       = 140;
    [ObservableProperty] private double _albumArtist = 130;
    [ObservableProperty] private double _genre       = 110;
    [ObservableProperty] private double _comment     = 120;
    [ObservableProperty] private double _fileName    = 220;

    /// <summary>
    /// What the title column works out to. The row does NOT bind this - its Grid gives the title a
    /// <c>*</c> column, so it lands on exactly this number as the remainder once the others are set.
    /// It is published because it takes part in the split and is worth being able to see.
    /// </summary>
    [ObservableProperty] private double _title = 300;

    /// <summary>
    /// Re-measure from the rows now listed. Cheap: one pass over the strings, no IO - every field it
    /// reads comes off the row, which since schema v3 means off the index (Chloe 2026-08-07: "null
    /// live zugriffe"). Call it when the listing changes, not per row.
    /// </summary>
    public void Measure(IReadOnlyList<TrackViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _weights.Clear();
        _remeasured = true;
        if (rows.Count == 0) return;

        _weights[TrackColumns.Title]       = P90(rows, r => r.Title);
        _weights[TrackColumns.Artist]      = P90(rows, r => r.Artist);
        _weights[TrackColumns.Album]       = P90(rows, r => r.AlbumText);
        _weights[TrackColumns.AlbumArtist] = P90(rows, r => r.AlbumArtistText);
        _weights[TrackColumns.Genre]       = P90(rows, r => r.GenreText);
        _weights[TrackColumns.Comment]     = P90(rows, r => r.CommentText);
        _weights[TrackColumns.FileName]    = P90(rows, r => r.FileNameText);
    }

    private static double P90(IReadOnlyList<TrackViewModel> rows, Func<TrackViewModel, string?> field) =>
        ColumnWidthFitter.Percentile(rows.Select(r => field(r)?.Length ?? 0));

    /// <summary>
    /// Hand out <paramref name="availableForText"/> px - what is left of the row after every FIXED
    /// column that is switched on. Only <paramref name="visible"/> columns take part; a column that
    /// is off must not reserve anything.
    /// </summary>
    public void Fit(double availableForText, TrackColumns visible)
    {
        ArgumentNullException.ThrowIfNull(visible);

        if (availableForText <= 0 || double.IsNaN(availableForText)) return;

        // (!) The caller is TrackRow.ArrangeOverride - every row of the list calls this with the same
        // number, and publishing a width triggers another layout pass. Same input + same weights =
        // same output, so it would settle anyway, but bailing out here means the 2nd..15,000th row of
        // a listing does no work at all and no second pass is provoked in the first place.
        if (!_remeasured && Math.Abs(availableForText - _available) < 0.5) return;

        _remeasured = false;
        _available = availableForText;

        // The title cell has no on/off switch - it is the one column every table always shows.
        var demands = Flexible
            .Where(key => key == TrackColumns.Title || visible.IsEnabled(key))
            .Select(key => new ColumnDemand(
                key,
                Weight: _weights.GetValueOrDefault(key),
                MinWidth: Floors.GetValueOrDefault(key, 60)))
            .ToList();

        if (demands.Count == 0) return;

        var widths = ColumnWidthFitter.Fit(availableForText, demands);

        double W(string key, double fallback) => widths.GetValueOrDefault(key, fallback);

        Title       = W(TrackColumns.Title, Title);
        Artist      = W(TrackColumns.Artist, Artist);
        Album       = W(TrackColumns.Album, Album);
        AlbumArtist = W(TrackColumns.AlbumArtist, AlbumArtist);
        Genre       = W(TrackColumns.Genre, Genre);
        Comment     = W(TrackColumns.Comment, Comment);
        FileName    = W(TrackColumns.FileName, FileName);
    }

    /// <summary>Re-run the last <see cref="Fit"/> - for a column being switched on or off, where the
    /// content has not changed but who is asking for space has.</summary>
    public void Refit(TrackColumns visible)
    {
        if (_available > 0) Fit(_available, visible);
    }
}
