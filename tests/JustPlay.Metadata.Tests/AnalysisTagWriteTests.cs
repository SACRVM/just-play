using JustPlay.Core.Models;
using JustPlay.Metadata;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// The suite's one composition of "what our detected values put in a file". It used to be written
/// out by hand in every app that writes analysis; these tests are what keep the extracted version
/// honest, because the rules it carries are the ones that are expensive to get wrong: a KEPT field
/// must survive, and the overwritten foreign value must be stashed so the write is reversible.
/// </summary>
public sealed class AnalysisTagWriteTests
{
    private static readonly AnalysisResult Detected = new()
    {
        Bpm          = 150.2,
        Key          = MusicalKey.TryParse("Am"),
        Energy       = 7,
        ReplayGainDb = -6.35,
        Peak         = 0.98,
    };

    [Fact]
    public void DetectedValuesLandInTheStandardTagsAndTheBlob()
    {
        var write = AnalysisTagWrite.ForDetected(Detected, null);

        Assert.NotNull(write);
        Assert.Equal(150.2, write!.Bpm);
        Assert.Equal(7, write.Energy);
        Assert.NotNull(write.Key);
        Assert.Equal(TrackAnalysisState.CurrentVersion, write.State!.Version);
        Assert.Equal(Detected, write.State.Detected);
    }

    /// <summary>ReplayGain is not a per-field decision - whenever there is a measurement, stamp it.</summary>
    [Fact]
    public void ReplayGainRidesAlong()
    {
        var write = AnalysisTagWrite.ForDetected(Detected, null);

        Assert.Equal(-6.35, write!.ReplayGainDb);
        Assert.Equal(0.98, write.Peak);
    }

    /// <summary>Nothing detected = nothing to write, and the file is not opened at all.</summary>
    [Fact]
    public void NothingDetectedWritesNothing()
    {
        Assert.Null(AnalysisTagWrite.ForDetected(AnalysisResult.Empty, null));
    }

    /// <summary>A write stamps the fields it wrote as Applied, so the conflict flags clear.</summary>
    [Fact]
    public void WrittenFieldsAreStampedApplied()
    {
        var state = AnalysisTagWrite.ForDetected(Detected, null)!.State!;

        Assert.Equal(FieldDecision.Applied, state.BpmDecision);
        Assert.Equal(FieldDecision.Applied, state.KeyDecision);
        Assert.Equal(FieldDecision.Applied, state.EnergyDecision);
    }

    /// <summary>
    /// (!!) The rule that costs a user their work when it is missing: a field they reviewed and KEPT
    /// is not ours to overwrite on the next analysis. You fix a wrong key, the track gets analysed
    /// again, and your fix must still be there.
    /// </summary>
    [Fact]
    public void AKeptFieldSurvivesAReAnalysis()
    {
        var current = new TrackMetadata
        {
            FallbackName = "track",
            TaggedKey = "5A",
            StoredAnalysis = new TrackAnalysisState { KeyDecision = FieldDecision.Kept },
        };

        var write = AnalysisTagWrite.ForDetected(Detected, current);

        Assert.Null(write!.Key);                                     // the tag is left alone
        Assert.Equal(FieldDecision.Kept, write.State!.KeyDecision);  // and it stays kept
        Assert.NotNull(write.Bpm);                                   // the other fields still land
    }

    /// <summary>The pre-overwrite foreign value is stashed once, so "restore original" has something
    /// to restore.</summary>
    [Fact]
    public void TheOverwrittenValueIsStashed()
    {
        var current = new TrackMetadata { FallbackName = "track", TaggedBpm = 75, TaggedKey = "5A", TaggedEnergy = 3 };

        var original = AnalysisTagWrite.ForDetected(Detected, current)!.State!.Original;

        Assert.Equal(75, original!.Bpm);
        Assert.Equal(3, original.Energy);
        Assert.NotNull(original.Key);
    }

    /// <summary>A SECOND write must not overwrite the stash with the value we ourselves wrote last
    /// time - that would lose the true origin for good.</summary>
    [Fact]
    public void ASecondWriteKeepsTheFirstStash()
    {
        var current = new TrackMetadata
        {
            FallbackName = "track",
            TaggedBpm = 150,   // what WE wrote last time
            StoredAnalysis = new TrackAnalysisState
            {
                Original = new AnalysisResult { Bpm = 75 },   // what the file really claimed
            },
        };

        var original = AnalysisTagWrite.ForDetected(Detected, current)!.State!.Original;

        Assert.Equal(75, original!.Bpm);
    }

    /// <summary>The comment is opt-in. Off, the user's comment is never touched.</summary>
    [Fact]
    public void TheCommentIsLeftAloneUnlessAskedFor()
    {
        var current = new TrackMetadata { FallbackName = "track", Comment = "ripped from vinyl" };

        Assert.Null(AnalysisTagWrite.ForDetected(Detected, current)!.Comment);
    }

    /// <summary>On, it is built from the values as they WILL BE in the tag, and it keeps the user's
    /// own text.</summary>
    [Fact]
    public void TheDjCommentIsBuiltFromTheValuesBeingWritten()
    {
        var current = new TrackMetadata { FallbackName = "track", Comment = "ripped from vinyl" };

        var comment = AnalysisTagWrite.ForDetected(Detected, current, djComment: true)!.Comment;

        Assert.NotNull(comment);
        Assert.Contains("Energy 7", comment);
        Assert.Contains("ripped from vinyl", comment);
    }

    /// <summary>The timestamp is the caller's measurement, never "now" invented here.</summary>
    [Fact]
    public void TheAnalysedAtStampIsTheCallers()
    {
        var when = new DateTime(2026, 8, 9, 21, 0, 0, DateTimeKind.Utc);

        Assert.Equal(when, AnalysisTagWrite.ForDetected(Detected, null, when)!.State!.AnalysedAtUtc);
    }

    /// <summary>With no fresh measurement it falls back to what the file already claimed, and stays
    /// null when nothing knows - "unknown" is treated as stale, and a guessed date would hide that.</summary>
    [Fact]
    public void AnUnknownAnalysedAtStaysUnknown()
    {
        Assert.Null(AnalysisTagWrite.ForDetected(Detected, null)!.State!.AnalysedAtUtc);
    }

    // -- The general per-field form ---------------------------------------------------------------

    [Fact]
    public void AllThreeFieldsUntouchedWritesNothing()
    {
        Assert.Null(AnalysisTagWrite.ForFields(
            Detected, null, TagFieldAction.None, TagFieldAction.None, TagFieldAction.None));
    }

    /// <summary>Keeping a field records the decision without touching the standard tag.</summary>
    [Fact]
    public void KeepStampsTheDecisionAndWritesNoValue()
    {
        var write = AnalysisTagWrite.ForFields(
            Detected, null, TagFieldAction.None, TagFieldAction.Keep, TagFieldAction.None);

        Assert.Null(write!.Key);
        Assert.Equal(FieldDecision.Kept, write.State!.KeyDecision);
    }

    /// <summary>An untouched field carries its stored decision over rather than resetting it.</summary>
    [Fact]
    public void AnUntouchedFieldKeepsItsPriorDecision()
    {
        var current = new TrackMetadata
        {
            FallbackName = "track",
            StoredAnalysis = new TrackAnalysisState { BpmDecision = FieldDecision.Applied },
        };

        var write = AnalysisTagWrite.ForFields(
            Detected, current, TagFieldAction.None, TagFieldAction.Keep, TagFieldAction.None);

        Assert.Equal(FieldDecision.Applied, write!.State!.BpmDecision);
    }
}
