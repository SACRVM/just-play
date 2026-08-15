using System;
using System.Collections.Generic;
using System.Linq;
using JustPlay.Core.Layout;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// The track table's content-aware column sizing: size every flexible text column
/// to the 90th percentile of what is actually in it, then share the leftover space out in proportion
/// to those widths - with floors, so the title column can never be squeezed.
/// </summary>
public class ColumnWidthFitterTests
{
    // -- Percentile ----------------------------------------------------------------------------

    [Fact]
    public void Percentile_ignores_the_long_outlier()
    {
        // 19 ordinary titles and one monster. Sizing to the MAX would hand the column 140 chars;
        // the point of the percentile is that it does not.
        var lengths = Enumerable.Repeat(20, 19).Append(140).ToList();

        Assert.Equal(20, ColumnWidthFitter.Percentile(lengths));
    }

    [Fact]
    public void Percentile_covers_at_least_the_asked_for_share()
    {
        var lengths = Enumerable.Range(1, 100).ToList(); // 1..100

        var p90 = ColumnWidthFitter.Percentile(lengths);

        Assert.Equal(90, p90);
        Assert.True(lengths.Count(v => v <= p90) >= 90);
    }

    [Fact]
    public void Percentile_of_an_empty_column_is_zero()
    {
        Assert.Equal(0, ColumnWidthFitter.Percentile(Array.Empty<int>()));
        Assert.Equal(0, ColumnWidthFitter.Percentile(new[] { 0, 0, 0 })); // all cells empty
    }

    [Fact]
    public void Percentile_of_a_single_row_is_that_row()
    {
        Assert.Equal(42, ColumnWidthFitter.Percentile(new[] { 42 }));
    }

    // -- Fit -----------------------------------------------------------------------------------

    [Fact]
    public void Fit_hands_out_every_pixel_it_was_given()
    {
        var columns = new List<ColumnDemand>
        {
            new("name", Weight: 40, MinWidth: 150),
            new("artist", Weight: 20, MinWidth: 60),
            new("genre", Weight: 10, MinWidth: 60),
        };

        var widths = ColumnWidthFitter.Fit(900, columns);

        Assert.Equal(900, widths.Values.Sum(), precision: 6);
    }

    [Fact]
    public void Fit_is_proportional_to_the_weights()
    {
        var columns = new List<ColumnDemand>
        {
            new("name", Weight: 30, MinWidth: 10),
            new("artist", Weight: 10, MinWidth: 10),
        };

        var widths = ColumnWidthFitter.Fit(800, columns);

        // 3 : 1, because the name column's content is typically three times as long.
        Assert.Equal(600, widths["name"], precision: 6);
        Assert.Equal(200, widths["artist"], precision: 6);
    }

    [Fact]
    public void Fit_never_squeezes_the_title_below_its_floor()
    {
        // Six wordy columns against a narrow table: proportionally the title would get ~85 px.
        var columns = new List<ColumnDemand>
        {
            new("name", Weight: 30, MinWidth: 150),
            new("artist", Weight: 25, MinWidth: 60),
            new("album", Weight: 25, MinWidth: 60),
            new("albumartist", Weight: 25, MinWidth: 60),
            new("genre", Weight: 25, MinWidth: 60),
            new("filename", Weight: 40, MinWidth: 60),
        };

        var widths = ColumnWidthFitter.Fit(560, columns);

        Assert.True(widths["name"] >= 150, $"title was squeezed to {widths["name"]}");
        foreach (var c in columns)
            Assert.True(widths[c.Key] >= c.MinWidth, $"{c.Key} fell below its floor");
        Assert.Equal(560, widths.Values.Sum(), precision: 6);
    }

    [Fact]
    public void Fit_pins_everything_to_its_floor_when_even_the_floors_do_not_fit()
    {
        var columns = new List<ColumnDemand>
        {
            new("name", Weight: 30, MinWidth: 150),
            new("artist", Weight: 20, MinWidth: 60),
            new("album", Weight: 20, MinWidth: 60),
        };

        var widths = ColumnWidthFitter.Fit(120, columns); // floors alone need 270

        Assert.Equal(150, widths["name"]);
        Assert.Equal(60, widths["artist"]);
        Assert.Equal(60, widths["album"]);
    }

    [Fact]
    public void Fit_still_gives_an_empty_column_its_floor_and_no_more()
    {
        // GENRE untagged across the whole view: it must not vanish, and it must not soak up space.
        var columns = new List<ColumnDemand>
        {
            new("name", Weight: 30, MinWidth: 150),
            new("genre", Weight: 0, MinWidth: 60),
        };

        var widths = ColumnWidthFitter.Fit(800, columns);

        Assert.Equal(60, widths["genre"], precision: 6);
        Assert.Equal(740, widths["name"], precision: 6);
    }

    [Fact]
    public void Fit_returns_a_width_for_every_column_it_was_asked_about()
    {
        var columns = new List<ColumnDemand>
        {
            new("name", Weight: 30, MinWidth: 150),
            new("artist", Weight: 0, MinWidth: 60),
            new("album", Weight: 5, MinWidth: 60),
            new("genre", Weight: 0, MinWidth: 60),
        };

        var widths = ColumnWidthFitter.Fit(700, columns);

        Assert.Equal(columns.Count, widths.Count);
        foreach (var c in columns) Assert.True(widths.ContainsKey(c.Key));
    }

    [Fact]
    public void Fit_of_nothing_is_nothing()
    {
        Assert.Empty(ColumnWidthFitter.Fit(900, Array.Empty<ColumnDemand>()));
    }

    [Fact]
    public void Fit_widens_every_column_when_the_window_grows()
    {
        var columns = new List<ColumnDemand>
        {
            new("name", Weight: 30, MinWidth: 150),
            new("artist", Weight: 20, MinWidth: 60),
            new("genre", Weight: 8, MinWidth: 60),
        };

        var narrow = ColumnWidthFitter.Fit(700, columns);
        var wide = ColumnWidthFitter.Fit(1400, columns);

        foreach (var c in columns)
            Assert.True(wide[c.Key] >= narrow[c.Key], $"{c.Key} shrank when the table got wider");
    }
}
