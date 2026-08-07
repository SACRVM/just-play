using JustPlay.Core.Models;
using JustPlay.Metadata;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// Unit tests for <see cref="DjCommentBuilder"/> - format, strip, and edge cases.
/// These are pure-logic tests with no file I/O.
/// </summary>
public class DjCommentBuilderTests
{
    // Convenience: A minor = pitch class 9, mode Minor -> Camelot "8A".
    private static readonly MusicalKey AMinor = new(9, KeyMode.Minor);
    // C major = pitch class 0, mode Major -> Camelot "8B".
    private static readonly MusicalKey CMajor = new(0, KeyMode.Major);

    // -- BuildSegment -------------------------------------------------------

    [Fact]
    public void BuildSegment_KeyAndEnergy_FullFormat()
    {
        var result = DjCommentBuilder.BuildSegment(AMinor, 7);
        Assert.Equal("8A - Energy 7", result);
    }

    [Fact]
    public void BuildSegment_KeyOnly_CamelotOnly()
    {
        var result = DjCommentBuilder.BuildSegment(AMinor, null);
        Assert.Equal("8A", result);
    }

    [Fact]
    public void BuildSegment_EnergyOnly_EnergyOnly()
    {
        var result = DjCommentBuilder.BuildSegment(null, 7);
        Assert.Equal("Energy 7", result);
    }

    [Fact]
    public void BuildSegment_NullBoth_ReturnsNull()
    {
        var result = DjCommentBuilder.BuildSegment(null, null);
        Assert.Null(result);
    }

    [Fact]
    public void BuildSegment_CMajor_CorrectCamelot()
    {
        // C major is Camelot 8B
        var result = DjCommentBuilder.BuildSegment(CMajor, 5);
        Assert.Equal("8B - Energy 5", result);
    }

    // -- Build (with existing comment handling) -----------------------------

    [Fact]
    public void Build_NoExistingComment_SegmentOnly()
    {
        var result = DjCommentBuilder.Build(AMinor, 7, null);
        Assert.Equal("8A - Energy 7", result);
    }

    [Fact]
    public void Build_EmptyExistingComment_SegmentOnly()
    {
        var result = DjCommentBuilder.Build(AMinor, 7, "");
        Assert.Equal("8A - Energy 7", result);
    }

    [Fact]
    public void Build_WithUserText_PreservesUserText()
    {
        var result = DjCommentBuilder.Build(AMinor, 7, "my note");
        Assert.Equal("8A - Energy 7 | my note", result);
    }

    [Fact]
    public void Build_NullKeyAndEnergy_ReturnsNull()
    {
        // Nothing to write - caller should leave comment untouched.
        var result = DjCommentBuilder.Build(null, null, "my note");
        Assert.Null(result);
    }

    // -- Idempotency (strip old prefix before re-prepending) ----------------

    [Fact]
    public void Build_Idempotent_WritingTwiceDoesNotDuplicate()
    {
        // Simulate: first write
        var afterFirstWrite = DjCommentBuilder.Build(AMinor, 7, "my note");
        Assert.Equal("8A - Energy 7 | my note", afterFirstWrite);

        // Simulate: second write (comment now starts with the old segment)
        var afterSecondWrite = DjCommentBuilder.Build(AMinor, 7, afterFirstWrite);
        Assert.Equal("8A - Energy 7 | my note", afterSecondWrite);
    }

    [Fact]
    public void Build_Idempotent_KeyOnlySegment()
    {
        var first = DjCommentBuilder.Build(AMinor, null, null);
        Assert.Equal("8A", first);

        var second = DjCommentBuilder.Build(AMinor, null, first);
        Assert.Equal("8A", second);
    }

    [Fact]
    public void Build_Idempotent_EnergyOnlySegment()
    {
        var first = DjCommentBuilder.Build(null, 7, "user text");
        Assert.Equal("Energy 7 | user text", first);

        var second = DjCommentBuilder.Build(null, 7, first);
        Assert.Equal("Energy 7 | user text", second);
    }

    [Fact]
    public void Build_Idempotent_UpdatedEnergyReplacesPreviousSegment()
    {
        // Energy changes from 7 to 9 on a re-write: old segment stripped, new one prepended.
        var first = DjCommentBuilder.Build(AMinor, 7, "my note");
        Assert.Equal("8A - Energy 7 | my note", first);

        var second = DjCommentBuilder.Build(AMinor, 9, first);
        Assert.Equal("8A - Energy 9 | my note", second);
    }

    // -- Strip --------------------------------------------------------------

    [Fact]
    public void Strip_FullSegmentWithUserText_ReturnsUserTextOnly()
    {
        var result = DjCommentBuilder.Strip("8A - Energy 7 | my note");
        Assert.Equal("my note", result);
    }

    [Fact]
    public void Strip_SegmentOnly_ReturnsEmpty()
    {
        var result = DjCommentBuilder.Strip("8A - Energy 7");
        Assert.Equal("", result);
    }

    [Fact]
    public void Strip_NoSegment_ReturnsOriginal()
    {
        var result = DjCommentBuilder.Strip("just a normal comment");
        Assert.Equal("just a normal comment", result);
    }

    [Fact]
    public void Strip_NullInput_ReturnsEmpty()
    {
        var result = DjCommentBuilder.Strip(null);
        Assert.Equal("", result);
    }

    [Fact]
    public void Strip_CamelotOnly_ReturnsEmpty()
    {
        var result = DjCommentBuilder.Strip("8A");
        Assert.Equal("", result);
    }

    [Fact]
    public void Strip_EnergyOnly_ReturnsEmpty()
    {
        var result = DjCommentBuilder.Strip("Energy 7");
        Assert.Equal("", result);
    }

    // -- Strip: legacy JP vibe blob (the cryptic "Romane" we're cleaning up) ---

    [Fact]
    public void Strip_JpBlobOnly_ReturnsEmpty()
    {
        var result = DjCommentBuilder.Strip("JP|E7|K8A|bpm140|gc.57|gr.14|pu.18|hy.02|dk.55|hx.41");
        Assert.Equal("", result);
    }

    [Fact]
    public void Strip_JpBlobWithUserText_ReturnsUserTextOnly()
    {
        var result = DjCommentBuilder.Strip("JP|E7|K8A|bpm140|gc.57 | banger");
        Assert.Equal("banger", result);
    }

    [Fact]
    public void Strip_BareJpPrefix_ReturnsEmpty()
    {
        var result = DjCommentBuilder.Strip("JP");
        Assert.Equal("", result);
    }

    [Fact]
    public void Strip_NonJpStartingComment_ReturnsOriginal()
    {
        // A user comment that merely starts with the letters "JP" (no pipe) must NOT be touched.
        var result = DjCommentBuilder.Strip("JPlayed this at Awakenings");
        Assert.Equal("JPlayed this at Awakenings", result);
    }

    // -- Build: rebuild a clean MIK comment from a file polluted with the JP blob --

    [Fact]
    public void Build_OverJpBlob_ProducesCleanMikComment()
    {
        var polluted = "JP|E7|K8A|bpm140|gc.57|gr.14|pu.18|hy.02|dk.55|hx.41";
        var result = DjCommentBuilder.Build(AMinor, 7, polluted);
        Assert.Equal("8A - Energy 7", result);
    }

    [Fact]
    public void Build_OverJpBlobWithUserText_KeepsUserText()
    {
        var polluted = "JP|E7|K8A|bpm140|gc.57 | banger";
        var result = DjCommentBuilder.Build(AMinor, 7, polluted);
        Assert.Equal("8A - Energy 7 | banger", result);
    }

    // -- N21 AIFF edge case: some AIFF files had "{MIK segment} | {JP blob}" --
    // The early batch writer produced this form for AIFF files. After the MIK
    // prefix is stripped, the JP blob should also be stripped from the tail.

    [Fact]
    public void Strip_MikPrefixThenJpBlob_BlobAlsoStripped()
    {
        // AIFF comment format: "{MIK segment} | {JP blob}"
        var aiffComment = "6A - Energy 6 | JP|E6|K6A|bpm126|gc.55|gr.25|pu.22|hy.00|dk.77|hx.29";
        var result = DjCommentBuilder.Strip(aiffComment);
        // After stripping the MIK prefix and then the JP tail, nothing remains.
        Assert.Equal("", result);
    }

    [Fact]
    public void Build_OverAiffMikPlusBlobComment_ProducesCleanMikComment()
    {
        // AIFF comment format: "{MIK segment} | {JP blob}"
        var aiffComment = "6A - Energy 6 | JP|E6|K6A|bpm126|gc.55|gr.25|pu.22|hy.00|dk.77|hx.29";
        var result = DjCommentBuilder.Build(AMinor, 7, aiffComment);
        // The result must NOT contain "JP|" - only the clean segment.
        Assert.Equal("8A - Energy 7", result);
        Assert.DoesNotContain("JP|", result);
    }

    [Fact]
    public void Build_OverAiffMikPlusBlobWithUserText_KeepsUserText()
    {
        // AIFF comment format: "{MIK segment} | {JP blob} | {user text}"
        // (hypothetical; the JP blob's " | " separator separates it from user text)
        var aiffComment = "6A - Energy 6 | JP|E6|K6A|bpm126|gc.55 | my note";
        var result = DjCommentBuilder.Build(AMinor, 7, aiffComment);
        // Strip: remove "6A - Energy 6 | " -> "JP|E6|K6A|bpm126|gc.55 | my note"
        // Strip: remove JP blob -> "my note"
        // Build: "8A - Energy 7 | my note"
        Assert.Equal("8A - Energy 7 | my note", result);
    }
}
