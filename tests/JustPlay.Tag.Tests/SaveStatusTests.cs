using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// The line a finished batch save leaves on screen, and the rule under it: a RENAME is not a WRITE.
///
/// <para>What was wrong: the rename sat inside the write's try block, so a file whose tags landed and
/// whose rename then failed was counted in BOTH columns - "Saved 12, 1 failed" out of 12 files - and
/// the news that actually mattered ("the tags did land") was the half that got dropped. The
/// single-file path had always said both halves; only the batch had not.</para>
/// </summary>
public class SaveStatusTests
{
    [Fact]
    public void A_clean_run_says_only_what_it_saved()
    {
        Assert.Equal("Saved 12.",
            TagEditorViewModel.SaveStatus(written: 12, deferred: 0, failed: 0,
                                          renamesHeld: 0, renamesFailed: 0));
    }

    [Fact]
    public void A_failed_rename_never_reads_as_a_failed_save()
    {
        var line = TagEditorViewModel.SaveStatus(written: 12, deferred: 0, failed: 0,
                                                 renamesHeld: 0, renamesFailed: 1);

        Assert.Equal("Saved 12 - 1 rename failed, its tags did land.", line);

        // The word that must not appear on its own: nothing here FAILED to save.
        Assert.DoesNotContain("1 failed", line);
    }

    /// <summary>The two are independent outcomes and both get said - the case that used to collapse
    /// into one wrong number.</summary>
    [Fact]
    public void A_real_failure_and_a_failed_rename_are_both_named()
    {
        Assert.Equal("Saved 10 - 2 failed - 1 rename failed, its tags did land.",
            TagEditorViewModel.SaveStatus(written: 10, deferred: 0, failed: 2,
                                          renamesHeld: 0, renamesFailed: 1));
    }

    /// <summary>A playing track's write is queued for the track change; its rename cannot ride along,
    /// because the queued write is aimed at the old path. Said, not silently skipped.</summary>
    [Fact]
    public void A_rename_held_by_playback_says_what_it_needs()
    {
        Assert.Equal(
            "Saved 4 - 1 playing, they save at the track change - 1 rename needs the track stopped.",
            TagEditorViewModel.SaveStatus(written: 4, deferred: 1, failed: 0,
                                          renamesHeld: 1, renamesFailed: 0));
    }

    [Fact]
    public void Several_of_each_read_as_plurals()
    {
        Assert.Equal("Saved 8 - 3 renames failed, their tags did land - 2 renames need the track stopped.",
            TagEditorViewModel.SaveStatus(written: 8, deferred: 0, failed: 0,
                                          renamesHeld: 2, renamesFailed: 3));
    }

    /// <summary>The report the CALLER reads to decide whether to keep its window open carries the
    /// rename separately too - it defaults to zero, so the transform window (which renames nothing)
    /// is unaffected.</summary>
    [Fact]
    public void The_report_keeps_the_two_apart()
    {
        var report = new TagSaveReport(12, 0, 0, 0, null, RenamesFailed: 1,
                                       FirstRenameError: "already exists");

        Assert.Equal(0, report.Failed);
        Assert.Equal(1, report.RenamesFailed);
        Assert.Equal("already exists", report.FirstRenameError);

        var noRenames = new TagSaveReport(12, 0, 0, 0, null);
        Assert.Equal(0, noRenames.RenamesFailed);
        Assert.Null(noRenames.FirstRenameError);
    }
}
