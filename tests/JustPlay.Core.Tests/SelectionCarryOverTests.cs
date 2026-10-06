using System.Collections.Generic;
using System.Linq;
using JustPlay.Core.Layout;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Carrying a list selection across a rebuild BY FILE PATH. The finder's rebuilds (a folder finishing
/// loading, the background check finding drift) used to keep the selection by object reference, so a
/// reload with new row objects lost it - and the tag editor that follows the selection jumped.
/// </summary>
public class SelectionCarryOverTests
{
    private sealed record Row(string Path);

    private static List<Row> Rows(params string[] paths) => paths.Select(p => new Row(p)).ToList();

    [Fact]
    public void Cursor_is_found_again_in_new_row_objects()
    {
        // The drift reload: same files, new objects.
        var rows = Rows(@"C:\m\a.mp3", @"C:\m\b.mp3", @"C:\m\c.mp3");

        var r = SelectionCarryOver.Restore(rows, x => x.Path, @"C:\m\b.mp3", [@"C:\m\b.mp3"]);

        Assert.Same(rows[1], r.Cursor);
        Assert.Equal([rows[1]], r.Selected);
    }

    [Fact]
    public void Paths_match_case_insensitively()
    {
        var rows = Rows(@"C:\M\A.mp3", @"C:\M\B.mp3");

        var r = SelectionCarryOver.Restore(rows, x => x.Path, @"c:\m\b.MP3", []);

        Assert.Same(rows[1], r.Cursor);
    }

    [Fact]
    public void Multi_selection_survives_in_list_order()
    {
        var rows = Rows("a", "b", "c", "d");

        var r = SelectionCarryOver.Restore(rows, x => x.Path, "c", ["d", "a", "c"]);

        Assert.Same(rows[2], r.Cursor);
        Assert.Equal([rows[0], rows[2], rows[3]], r.Selected);
    }

    [Fact]
    public void A_vanished_cursor_moves_to_a_surviving_selected_row()
    {
        var rows = Rows("a", "b", "c");

        var r = SelectionCarryOver.Restore(rows, x => x.Path, "gone", ["gone", "c"]);

        Assert.Same(rows[2], r.Cursor);
        Assert.Equal([rows[2]], r.Selected);
    }

    [Fact]
    public void Nothing_surviving_falls_back_to_the_first_row()
    {
        var rows = Rows("a", "b");

        var r = SelectionCarryOver.Restore(rows, x => x.Path, "gone", ["gone"]);

        Assert.Same(rows[0], r.Cursor);
        Assert.Equal([rows[0]], r.Selected);
    }

    [Fact]
    public void An_empty_list_selects_nothing()
    {
        var r = SelectionCarryOver.Restore(new List<Row>(), x => x.Path, "a", ["a"]);

        Assert.Null(r.Cursor);
        Assert.Empty(r.Selected);
    }

    [Fact]
    public void A_file_listed_twice_is_selected_once()
    {
        // A playlist may name the same file twice; two rows of one file must not become a
        // "multi-file" selection.
        var rows = Rows("a", "b", "a");

        var r = SelectionCarryOver.Restore(rows, x => x.Path, "a", ["a"]);

        Assert.Same(rows[0], r.Cursor);
        Assert.Equal([rows[0]], r.Selected);
    }

    [Fact]
    public void SamePaths_ignores_order_repeats_and_case()
    {
        Assert.True(SelectionCarryOver.SamePaths([@"C:\a", @"C:\b"], [@"c:\B", @"c:\A", @"C:\a"]));
        Assert.True(SelectionCarryOver.SamePaths([], []));
        Assert.False(SelectionCarryOver.SamePaths([@"C:\a"], [@"C:\a", @"C:\b"]));
        Assert.False(SelectionCarryOver.SamePaths([@"C:\a"], []));
    }
}
