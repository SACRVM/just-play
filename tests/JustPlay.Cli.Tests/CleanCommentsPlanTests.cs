using JustPlay.Cli.Commands;
using JustPlay.Core.Models;

namespace JustPlay.Cli.Tests;

/// <summary>
/// What <c>justplay tag clean</c> decides for one file, from its tags alone - no file is opened.
/// </summary>
public sealed class CleanCommentsPlanTests
{
    [Fact]
    public void A_file_that_could_not_be_read_is_not_planned_on_even_with_force()
    {
        // The reader returns a bare record for a locked file or a share that hiccuped.
        var bare = new TrackMetadata { FallbackName = "track", ReadFailed = true };

        Assert.Null(CleanCommentsCommand.Plan(bare, force: true));
        Assert.Null(CleanCommentsCommand.Plan(bare, force: false));
    }

    [Fact]
    public void The_same_empty_tags_read_fine_are_rewritten_under_force()
    {
        // Why the guard above matters: planned on, a bare record yields an EMPTY comment, and --force
        // writes it. For a file that really has no comment that is harmless; for one whose read
        // failed it would wipe the comment the file actually carries.
        var untagged = new TrackMetadata { FallbackName = "track" };

        var plan = CleanCommentsCommand.Plan(untagged, force: true);

        Assert.NotNull(plan);
        Assert.True(plan!.ForceRewrite);
        Assert.Equal("", plan.NewComment);
    }

    [Fact]
    public void A_legacy_vibe_blob_is_planned_into_the_clean_comment()
    {
        var meta = new TrackMetadata
        {
            FallbackName = "track",
            Comment      = "JP|E7|K8A|bpm140 | my note",
            Grouping     = "JP|E7|K8A|bpm140",
            TaggedKey    = "8A",
            TaggedEnergy = 7,
        };

        var plan = CleanCommentsCommand.Plan(meta, force: false);

        Assert.NotNull(plan);
        Assert.Equal("8A - Energy 7 | my note", plan!.NewComment);
        Assert.Equal("", plan.NewGrouping);
        Assert.True(plan.CommentChanged);
        Assert.True(plan.GroupingChanged);
        Assert.False(plan.NothingToDo);
    }

    [Fact]
    public void An_already_clean_file_needs_nothing()
    {
        var meta = new TrackMetadata
        {
            FallbackName = "track",
            Comment      = "8A - Energy 7",
            TaggedKey    = "8A",
            TaggedEnergy = 7,
        };

        Assert.True(CleanCommentsCommand.Plan(meta, force: false)!.NothingToDo);
    }
}
