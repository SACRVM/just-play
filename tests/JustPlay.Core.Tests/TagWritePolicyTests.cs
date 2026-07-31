using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Pins the two invariants <see cref="TagWritePolicy"/> and <see cref="TagWritePlan"/> depend on
/// for the rest of the milestone to build on safely:
///   1. the default policy allows EVERY family — a user who never opens the (future) policy
///      screen sees zero behaviour change, because today's writer has no gate at all;
///   2. <see cref="TagWritePlan.HasChanges"/> / <see cref="TagWritePlan.HasOverwrites"/> reduce
///      a field list to exactly the summary a preview UI needs.
/// No TagLib# involved — these are pure Core-model tests; see
/// <c>JustPlay.Metadata.Tests.TagLibWritePreviewTests</c> for the real-file integration tests.
/// </summary>
public class TagWritePolicyTests
{
    private static readonly TagFrameFamily[] AllFamilies = Enum.GetValues<TagFrameFamily>();

    [Fact]
    public void Defaults_AllowEveryFamily_MatchingTodaysUngatedWriter()
    {
        var policy = new TagWritePolicy();

        foreach (var family in AllFamilies)
            Assert.True(policy.Allows(family), $"{family} must default to allowed.");
    }

    [Fact]
    public void AllowAll_IsEquivalentToTheDefaultConstructor()
    {
        // Record equality: AllowAll must not be a hand-tweaked "mostly true" policy that could
        // silently drift from what `new TagWritePolicy()` actually defaults to.
        Assert.Equal(new TagWritePolicy(), TagWritePolicy.AllowAll);
    }

    [Theory]
    [InlineData(TagFrameFamily.JustPlayBlob)]
    [InlineData(TagFrameFamily.Bpm)]
    [InlineData(TagFrameFamily.Key)]
    [InlineData(TagFrameFamily.Energy)]
    [InlineData(TagFrameFamily.Comment)]
    [InlineData(TagFrameFamily.Grouping)]
    [InlineData(TagFrameFamily.Rating)]
    [InlineData(TagFrameFamily.ReplayGain)]
    public void Allows_TurningOffOneFamily_LeavesEveryOtherFamilyAllowed(TagFrameFamily disabled)
    {
        var policy = Disable(TagWritePolicy.AllowAll, disabled);

        foreach (var family in AllFamilies)
            Assert.Equal(family != disabled, policy.Allows(family));
    }

    [Fact]
    public void Allows_UnknownEnumValue_ThrowsRatherThanSilentlyAllowingOrDenying()
    {
        var policy = TagWritePolicy.AllowAll;
        var bogus = (TagFrameFamily)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Allows(bogus));
    }

    private static TagWritePolicy Disable(TagWritePolicy policy, TagFrameFamily family) => family switch
    {
        TagFrameFamily.JustPlayBlob => policy with { AllowJustPlayBlob = false },
        TagFrameFamily.Bpm          => policy with { AllowBpm = false },
        TagFrameFamily.Key          => policy with { AllowKey = false },
        TagFrameFamily.Energy       => policy with { AllowEnergy = false },
        TagFrameFamily.Comment      => policy with { AllowComment = false },
        TagFrameFamily.Grouping     => policy with { AllowGrouping = false },
        TagFrameFamily.Rating       => policy with { AllowRating = false },
        TagFrameFamily.ReplayGain   => policy with { AllowReplayGain = false },
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };
}

/// <summary>Tests for the <see cref="TagWritePlan"/> summary properties in isolation from any
/// real file — the plan is just a list, so these build it by hand.</summary>
public class TagWritePlanTests
{
    private static TagFieldPlan Field(TagWriteAction action) => new()
    {
        Family = TagFrameFamily.Bpm,
        FieldName = "BPM",
        ProposedValue = "128",
        Action = action,
    };

    [Fact]
    public void HasChanges_TrueWhenAnyFieldIsWriteOrOverwrite()
    {
        var writePlan = new TagWritePlan { FilePath = "x", Fields = [Field(TagWriteAction.Write)] };
        var overwritePlan = new TagWritePlan { FilePath = "x", Fields = [Field(TagWriteAction.Overwrite)] };

        Assert.True(writePlan.HasChanges);
        Assert.True(overwritePlan.HasChanges);
    }

    [Fact]
    public void HasChanges_FalseWhenEveryFieldIsUnchangedOrSkipped()
    {
        var plan = new TagWritePlan
        {
            FilePath = "x",
            Fields = [Field(TagWriteAction.Unchanged), Field(TagWriteAction.SkippedByPolicy)],
        };

        Assert.False(plan.HasChanges);
        Assert.False(plan.HasOverwrites);
    }

    [Fact]
    public void HasOverwrites_TrueOnlyForOverwriteAction()
    {
        var plan = new TagWritePlan
        {
            FilePath = "x",
            Fields = [Field(TagWriteAction.Write), Field(TagWriteAction.Overwrite)],
        };

        Assert.True(plan.HasChanges);
        Assert.True(plan.HasOverwrites);
    }

    [Fact]
    public void EmptyFieldList_HasNeitherChangesNorOverwrites()
    {
        var plan = new TagWritePlan { FilePath = "x", Fields = [] };

        Assert.False(plan.HasChanges);
        Assert.False(plan.HasOverwrites);
    }
}
