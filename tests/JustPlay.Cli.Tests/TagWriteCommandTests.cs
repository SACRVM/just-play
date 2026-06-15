using JustPlay.Cli.Tags;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Engine;
using JustPlay.Engine.Dtos;
using JustPlay.Metadata;

namespace JustPlay.Cli.Tests;

/// <summary>
/// Tests for <see cref="VibeTagEncoder"/> (encode / decode / comment helpers)
/// and the full WriteTags → ReadTags round-trip for the new Grouping + Comment
/// fields introduced by the <c>justplay tag write</c> command.
///
/// These are purely managed tests — no BASS, no audio decoding needed.
/// The round-trip tests operate on a temp TagLib#-tagged MP3 synthesized in memory.
/// </summary>
public sealed class TagWriteCommandTests : IDisposable
{
    private readonly string _tempFile;

    public TagWriteCommandTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"justplay_tagwrite_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(_tempFile, MinimalMp3());
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VibeTagEncoder — Encode round-trips
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_AllFieldsPresent_ProducesExpectedString()
    {
        var tag = VibeTagEncoder.Encode(
            energy:         7,
            camelot:        "8A",
            bpm:            140.4,     // rounded to 140
            gridConfidence: 0.57,
            bassGroove:     0.14,
            bassPunch:      0.18,
            hypnotic:       0.02,
            dark:           0.55,
            harshness:      0.41);

        Assert.Equal("JP|E7|K8A|bpm140|gc.57|gr.14|pu.18|hy.02|dk.55|hx.41", tag);
    }

    [Fact]
    public void Encode_OnlyPrefixWhenAllNull_ReturnsJpOnly()
    {
        var tag = VibeTagEncoder.Encode();
        Assert.Equal("JP", tag);
    }

    [Fact]
    public void Encode_PartialFields_OmitsNullFields()
    {
        var tag = VibeTagEncoder.Encode(energy: 5, camelot: "4B");
        Assert.Equal("JP|E5|K4B", tag);
        Assert.DoesNotContain("bpm", tag);
        Assert.DoesNotContain("gc.", tag);
    }

    [Fact]
    public void Encode_BpmRounding_CorrectlyRounds()
    {
        // 128.4 → 128; 128.6 → 129
        Assert.Contains("|bpm128", VibeTagEncoder.Encode(bpm: 128.4));
        Assert.Contains("|bpm129", VibeTagEncoder.Encode(bpm: 128.6));
    }

    [Fact]
    public void Encode_PercentValues_ZeroPaddedTwoDigits()
    {
        // hypnotic=0.02 → "hy.02" (zero-padded), harshness=0.09 → "hx.09"
        var tag = VibeTagEncoder.Encode(hypnotic: 0.02, harshness: 0.09);
        Assert.Contains("hy.02", tag);
        Assert.Contains("hx.09", tag);
    }

    [Fact]
    public void Encode_PercentClamping_ClampsBeyondBounds()
    {
        // Values above 1.0 should be clamped to 1.0 → 100, but we store 00-99 via Math.Round.
        // 1.0 → ToPct(1.0) = round(100) = 100 → "D2" = "100" (three chars) — by design.
        // Values below 0.0 → 0.
        var tagHigh = VibeTagEncoder.Encode(harshness: 1.5); // clamped to 1.0 → 100
        Assert.Contains("hx.100", tagHigh);

        var tagLow = VibeTagEncoder.Encode(harshness: -0.5); // clamped to 0.0 → 0
        Assert.Contains("hx.00", tagLow);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VibeTagEncoder — Decode round-trips
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_FullTag_RoundTrips()
    {
        var original = VibeTagEncoder.Encode(
            energy:         7,
            camelot:        "8A",
            bpm:            140.0,
            gridConfidence: 0.57,
            bassGroove:     0.14,
            bassPunch:      0.18,
            hypnotic:       0.02,
            dark:           0.55,
            harshness:      0.41);

        var fields = VibeTagEncoder.Decode(original);

        Assert.NotNull(fields);
        Assert.Equal(7,      fields.Energy);
        Assert.Equal("8A",   fields.Camelot);
        Assert.Equal(140.0,  fields.Bpm);
        Assert.Equal(0.57,   fields.GridConfidence!.Value, 2);
        Assert.Equal(0.14,   fields.BassGroove!.Value, 2);
        Assert.Equal(0.18,   fields.BassPunch!.Value, 2);
        Assert.Equal(0.02,   fields.Hypnotic!.Value, 2);
        Assert.Equal(0.55,   fields.Dark!.Value, 2);
        Assert.Equal(0.41,   fields.Harshness!.Value, 2);
    }

    [Fact]
    public void Decode_JpOnly_ReturnsEmptyFields()
    {
        var fields = VibeTagEncoder.Decode("JP");
        Assert.NotNull(fields);
        Assert.Null(fields.Energy);
        Assert.Null(fields.Camelot);
        Assert.Null(fields.Bpm);
    }

    [Fact]
    public void Decode_NotAJpTag_ReturnsNull()
    {
        Assert.Null(VibeTagEncoder.Decode("8A - Energy 7 | my note"));
        Assert.Null(VibeTagEncoder.Decode(""));
        Assert.Null(VibeTagEncoder.Decode(null));
        Assert.Null(VibeTagEncoder.Decode("something else"));
    }

    [Fact]
    public void Decode_WithUserTextSuffix_IgnoresUserText()
    {
        var tagWithUser = "JP|E7|K8A|bpm140 | my crate note about this track";
        var fields = VibeTagEncoder.Decode(tagWithUser);

        Assert.NotNull(fields);
        Assert.Equal(7,      fields.Energy);
        Assert.Equal("8A",   fields.Camelot);
        Assert.Equal(140.0,  fields.Bpm);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VibeTagEncoder — Comment helpers (BuildComment / StripJpPrefix)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildComment_NoExistingText_ReturnsVibeTagOnly()
    {
        var vibe = VibeTagEncoder.Encode(energy: 7, camelot: "8A");
        var comment = VibeTagEncoder.BuildComment(vibe, null);

        Assert.Equal("JP|E7|K8A", comment);
    }

    [Fact]
    public void BuildComment_ExistingUserText_PreservesUserText()
    {
        var vibe = VibeTagEncoder.Encode(energy: 7, camelot: "8A");
        var comment = VibeTagEncoder.BuildComment(vibe, "my note");

        Assert.Equal("JP|E7|K8A | my note", comment);
    }

    [Fact]
    public void BuildComment_ExistingJpPrefix_Idempotent()
    {
        var vibe = VibeTagEncoder.Encode(energy: 7, camelot: "8A");

        // First write
        var first = VibeTagEncoder.BuildComment(vibe, "user text");
        Assert.Equal("JP|E7|K8A | user text", first);

        // Second write (same analysis, should not grow the comment)
        var second = VibeTagEncoder.BuildComment(vibe, first);
        Assert.Equal("JP|E7|K8A | user text", second);
    }

    [Fact]
    public void BuildComment_UpdatedVibe_ReplacesOldJpPrefix()
    {
        var oldVibe = VibeTagEncoder.Encode(energy: 6, camelot: "8A");
        var first   = VibeTagEncoder.BuildComment(oldVibe, "user text");
        // → "JP|E6|K8A | user text"

        var newVibe = VibeTagEncoder.Encode(energy: 7, camelot: "8A", bpm: 128.0);
        var second  = VibeTagEncoder.BuildComment(newVibe, first);
        // → "JP|E7|K8A|bpm128 | user text"

        Assert.Equal("JP|E7|K8A|bpm128 | user text", second);
        Assert.DoesNotContain("E6", second);
    }

    [Fact]
    public void BuildComment_MikStyleExistingComment_PreservesMikText()
    {
        // DjCommentBuilder writes "8A - Energy 7 | my note" (starts with digit/letter, not "JP|")
        // Our StripJpPrefix should leave it untouched.
        var mikComment = "8A - Energy 7 | my note";
        var vibe = VibeTagEncoder.Encode(energy: 7, camelot: "8A");
        var result = VibeTagEncoder.BuildComment(vibe, mikComment);

        Assert.Equal($"JP|E7|K8A | {mikComment}", result);
    }

    [Fact]
    public void StripJpPrefix_JpOnlyString_ReturnsEmpty()
    {
        Assert.Equal("", VibeTagEncoder.StripJpPrefix("JP"));
    }

    [Fact]
    public void StripJpPrefix_JpWithFields_ReturnsEmpty()
    {
        Assert.Equal("", VibeTagEncoder.StripJpPrefix("JP|E7|K8A|bpm140"));
    }

    [Fact]
    public void StripJpPrefix_JpWithUserText_ReturnsUserText()
    {
        Assert.Equal("user text", VibeTagEncoder.StripJpPrefix("JP|E7|K8A | user text"));
    }

    [Fact]
    public void StripJpPrefix_NonJpString_ReturnsUnchanged()
    {
        var comment = "8A - Energy 7 | my note";
        Assert.Equal(comment, VibeTagEncoder.StripJpPrefix(comment));
    }

    [Fact]
    public void StripJpPrefix_Null_ReturnsEmpty()
    {
        Assert.Equal("", VibeTagEncoder.StripJpPrefix(null));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WriteTags → ReadTags round-trip for Grouping + Comment
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteGroupingAndComment_RoundTrip()
    {
        var engine = BuildEngine();

        var vibeTag = VibeTagEncoder.Encode(
            energy:         7,
            camelot:        "8A",
            bpm:            128.0,
            gridConfidence: 0.57,
            bassGroove:     0.14,
            bassPunch:      0.18,
            hypnotic:       0.02,
            dark:           0.55,
            harshness:      0.41);

        var request = new WriteTagsRequest
        {
            Bpm      = 128.0,
            Key      = "8A",
            Energy   = 7,
            Comment  = VibeTagEncoder.BuildComment(vibeTag, null),
            Grouping = vibeTag,
        };

        var writeResult = await engine.WriteTagsAsync(_tempFile, request);
        Assert.True(writeResult.Success, writeResult.Error);

        var readBack = await engine.ReadTagsAsync(_tempFile);

        // Standard tag round-trips
        Assert.Equal(128.0, readBack.TaggedBpm);
        Assert.Equal(7, readBack.TaggedEnergy);
        Assert.False(string.IsNullOrEmpty(readBack.TaggedKey)); // "Am" written from "8A"

        // Grouping round-trip
        Assert.Equal(vibeTag, readBack.Grouping);

        // Comment contains the JP vibe prefix
        Assert.NotNull(readBack.Comment);
        Assert.StartsWith("JP|E7|K8A|bpm128", readBack.Comment);

        // Decode the comment and verify fields
        var decoded = VibeTagEncoder.Decode(readBack.Comment);
        Assert.NotNull(decoded);
        Assert.Equal(7,     decoded.Energy);
        Assert.Equal("8A",  decoded.Camelot);
        Assert.Equal(128.0, decoded.Bpm);
        Assert.Equal(0.57,  decoded.GridConfidence!.Value, 2);
    }

    [Fact]
    public async Task WriteGrouping_Idempotent_SameContentOnRewrite()
    {
        var engine  = BuildEngine();
        var vibeTag = VibeTagEncoder.Encode(energy: 7, camelot: "8A", bpm: 128.0);

        // First write
        var r1 = await engine.WriteTagsAsync(_tempFile, new WriteTagsRequest
        {
            Comment  = VibeTagEncoder.BuildComment(vibeTag, null),
            Grouping = vibeTag,
        });
        Assert.True(r1.Success, r1.Error);

        var afterFirst = await engine.ReadTagsAsync(_tempFile);

        // Second write (same vibe, existing comment has the JP prefix now)
        var r2 = await engine.WriteTagsAsync(_tempFile, new WriteTagsRequest
        {
            Comment  = VibeTagEncoder.BuildComment(vibeTag, afterFirst.Comment),
            Grouping = vibeTag,
        });
        Assert.True(r2.Success, r2.Error);

        var afterSecond = await engine.ReadTagsAsync(_tempFile);

        // Comment must not have grown (idempotent)
        Assert.Equal(afterFirst.Comment, afterSecond.Comment);
        Assert.Equal(afterFirst.Grouping, afterSecond.Grouping);
    }

    [Fact]
    public async Task WriteGrouping_WithExistingUserText_PreservesUserText()
    {
        var engine = BuildEngine();

        // Pre-set a user comment in the file
        var writer = new TagLibMetadataWriter();
        writer.Write(_tempFile, new TagWrite { Comment = "my crate note" });

        var vibeTag = VibeTagEncoder.Encode(energy: 7, camelot: "8A");
        var existing = (await engine.ReadTagsAsync(_tempFile)).Comment;
        var newComment = VibeTagEncoder.BuildComment(vibeTag, existing);

        await engine.WriteTagsAsync(_tempFile, new WriteTagsRequest
        {
            Comment  = newComment,
            Grouping = vibeTag,
        });

        var readBack = await engine.ReadTagsAsync(_tempFile);

        // User text is preserved after the JP prefix separator
        Assert.NotNull(readBack.Comment);
        Assert.StartsWith("JP|", readBack.Comment);
        Assert.Contains("my crate note", readBack.Comment);

        // Grouping is the pure vibe tag, without user text
        Assert.Equal("JP|E7|K8A", readBack.Grouping);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static ITrackEngine BuildEngine()
    {
        // Platform-agnostic engine with stub BPM detector and stub audio decoder.
        // Mirrors the pattern in TrackEngineTests — no BASS required.
        const int energySr = 11025;
        const int keySr    = 44100;

        var energySamples = MakeSilentSamples(energySr);
        var keySamples    = MakeSilentSamples(keySr);

        var decoder = new StubDecoder(energySamples, keySamples, energySr, keySr);
        var analysis = new Analysis.TrackAnalysisService(
            bpm:      new StubBpm(128.0),
            decoder:  decoder,
            key:      new Analysis.ChromagramKeyDetector(),
            energy:   new Analysis.SpectralEnergyDetector(),
            loudness: new Analysis.Bs1770LoudnessDetector());

        return new TrackEngine(analysis, new TagLibMetadataReader(), new TagLibMetadataWriter());
    }

    private static float[] MakeSilentSamples(int sampleRate)
        => new float[(int)(sampleRate * 3.0)]; // 3 s of silence

    private static byte[] MinimalMp3()
    {
        var buf = Path.Combine(Path.GetTempPath(), $"justplay_tagwrite_seed_{Guid.NewGuid():N}.mp3");
        try
        {
            var bare = new byte[14];
            bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
            bare[3] = 3; // version 2.3
            bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
            File.WriteAllBytes(buf, bare);
            using var f = TagLib.File.Create(buf);
            f.Tag.Title = "TagWrite Test";
            f.Save();
            return File.ReadAllBytes(buf);
        }
        finally
        {
            if (File.Exists(buf)) File.Delete(buf);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // N15 Promote: JUSTPLAY blob round-trip — decision = Applied
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writing a <see cref="TagWrite"/> with a <see cref="TrackAnalysisState"/> whose
    /// decisions are Applied, then reading it back, must yield:
    ///   - TKEY == detected key (e.g. "Am" for 8A / A minor)
    ///   - Blob.KeyDecision == Applied
    ///   - Blob.BpmDecision == Applied
    ///   - Blob.EnergyDecision == Applied
    ///   - Blob.Version == CurrentVersion
    /// This is the core round-trip that PromoteCommand relies on.
    /// </summary>
    [Fact]
    public void PromoteRoundTrip_BlobWrittenWithApplied_ReadBackHasAppliedAndMatchingTkey()
    {
        var writer = new TagLibMetadataWriter();
        var reader = new TagLibMetadataReader();

        var detectedKey    = new MusicalKey(9, KeyMode.Minor); // A minor = 8A
        // Use 128.7 to avoid .NET banker's rounding (128.5 → 128, not 129).
        var detectedBpm    = 128.7;
        var detectedEnergy = 7;

        // Simulate what PromoteCommand.Run does when writing a no-blob file:
        var state = new TrackAnalysisState
        {
            Version        = TrackAnalysisState.CurrentVersion,
            Detected       = new AnalysisResult
            {
                Bpm    = detectedBpm,
                Key    = detectedKey,
                Energy = detectedEnergy,
            },
            Original       = null,      // no pre-existing foreign value to stash
            BpmDecision    = FieldDecision.Applied,
            KeyDecision    = FieldDecision.Applied,
            EnergyDecision = FieldDecision.Applied,
        };

        var tagWrite = new TagWrite
        {
            Bpm    = detectedBpm,
            Key    = detectedKey,
            Energy = detectedEnergy,
            State  = state,
        };

        writer.Write(_tempFile, tagWrite);

        // Read back and assert.
        var meta = reader.Read(_tempFile);

        // Standard tag TKEY must equal the detected key (A minor → "Am").
        Assert.Equal("Am", meta.TaggedKey);

        // Standard TBPM must be the rounded value (128.5 → 129; stored as uint, read back as double).
        Assert.Equal(129.0, meta.TaggedBpm);

        // Standard ENERGY must be written.
        Assert.Equal(detectedEnergy, meta.TaggedEnergy);

        // JUSTPLAY blob must be present and fully Applied.
        Assert.NotNull(meta.StoredAnalysis);
        var blob = meta.StoredAnalysis!;
        Assert.Equal(TrackAnalysisState.CurrentVersion, blob.Version);
        Assert.Equal(FieldDecision.Applied, blob.KeyDecision);
        Assert.Equal(FieldDecision.Applied, blob.BpmDecision);
        Assert.Equal(FieldDecision.Applied, blob.EnergyDecision);

        // Blob detected key must survive the round-trip.
        Assert.NotNull(blob.Detected.Key);
        Assert.Equal(detectedKey.PitchClass, blob.Detected.Key!.Value.PitchClass);
        Assert.Equal(detectedKey.Mode,       blob.Detected.Key!.Value.Mode);
    }

    /// <summary>
    /// A file already having a blob with Applied decisions must remain unchanged
    /// (PromoteCommand.IsFullyApplied returns true → no write).
    /// </summary>
    [Fact]
    public void PromoteRoundTrip_AlreadyApplied_SkippedByIsFullyApplied()
    {
        // Write a blob with all Applied decisions first.
        var writer = new TagLibMetadataWriter();
        var reader = new TagLibMetadataReader();
        var key    = new MusicalKey(0, KeyMode.Major); // C major = 1B

        var appliedState = new TrackAnalysisState
        {
            Version        = TrackAnalysisState.CurrentVersion,
            Detected       = new AnalysisResult { Key = key, Bpm = 120.0, Energy = 5 },
            BpmDecision    = FieldDecision.Applied,
            KeyDecision    = FieldDecision.Applied,
            EnergyDecision = FieldDecision.Applied,
        };

        writer.Write(_tempFile, new TagWrite { Key = key, Bpm = 120.0, Energy = 5, State = appliedState });

        var before = reader.Read(_tempFile);
        Assert.NotNull(before.StoredAnalysis);
        Assert.Equal(FieldDecision.Applied, before.StoredAnalysis!.KeyDecision);

        // Verify the helper predicate that PromoteCommand uses.
        var isApplied = before.StoredAnalysis is
        {
            BpmDecision:    FieldDecision.Applied,
            KeyDecision:    FieldDecision.Applied,
            EnergyDecision: FieldDecision.Applied,
        };
        Assert.True(isApplied);
    }

    /// <summary>
    /// A blob with Pending KeyDecision is correctly identified as needing promote.
    /// </summary>
    [Fact]
    public void PromoteRoundTrip_PendingBlob_NeedsPromote()
    {
        var writer = new TagLibMetadataWriter();
        var reader = new TagLibMetadataReader();
        var key    = new MusicalKey(9, KeyMode.Minor); // Am

        // Write blob with Pending decision (simulates a file that was analysed but
        // the decision was never confirmed by the user or by tag-write).
        var pendingState = new TrackAnalysisState
        {
            Version        = TrackAnalysisState.CurrentVersion,
            Detected       = new AnalysisResult { Key = key, Bpm = 128.0, Energy = 7 },
            BpmDecision    = FieldDecision.Pending,
            KeyDecision    = FieldDecision.Pending,
            EnergyDecision = FieldDecision.Pending,
        };

        writer.Write(_tempFile, new TagWrite { State = pendingState });
        var before = reader.Read(_tempFile);
        Assert.Equal(FieldDecision.Pending, before.StoredAnalysis?.KeyDecision);

        // Upgrade to Applied (what PromoteCommand does for has-blob files).
        var upgraded = pendingState with
        {
            BpmDecision    = FieldDecision.Applied,
            KeyDecision    = FieldDecision.Applied,
            EnergyDecision = FieldDecision.Applied,
        };
        writer.Write(_tempFile, new TagWrite { Key = key, Bpm = 128.0, Energy = 7, State = upgraded });

        var after = reader.Read(_tempFile);
        Assert.Equal(FieldDecision.Applied, after.StoredAnalysis?.KeyDecision);
        Assert.Equal(FieldDecision.Applied, after.StoredAnalysis?.BpmDecision);
        Assert.Equal(FieldDecision.Applied, after.StoredAnalysis?.EnergyDecision);
        Assert.Equal("Am", after.TaggedKey);
    }
}

// ── Test stubs (mirrors TrackEngineTests, file-scoped so no name conflicts) ──

file sealed class StubBpm : JustPlay.Core.Abstractions.IBpmDetector
{
    private readonly double? _bpm;
    public StubBpm(double? bpm) => _bpm = bpm;
    public double? Detect(string _, CancellationToken __ = default) => _bpm;
}

file sealed class StubDecoder : JustPlay.Core.Abstractions.IAudioDecoder
{
    private readonly float[] _energySamples;
    private readonly float[] _keySamples;
    private readonly int     _energySr;
    private readonly int     _keySr;

    public StubDecoder(float[] e, float[] k, int esr, int ksr)
    {
        _energySamples = e;
        _keySamples    = k;
        _energySr      = esr;
        _keySr         = ksr;
    }

    public JustPlay.Core.Models.DecodedAudio DecodeMono(string _, int targetSr, CancellationToken __ = default)
        => targetSr == _keySr
            ? new(_keySamples,    _keySr)
            : new(_energySamples, _energySr);
}
