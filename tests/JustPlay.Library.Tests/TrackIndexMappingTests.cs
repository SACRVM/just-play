using JustPlay.Core.Models;

namespace JustPlay.Library.Tests;

/// <summary>
/// L7 (night report 2026-08-01, L6 follow-up): <see cref="TrackAnalysisState.AnalysedAtUtc"/>
/// round-tripping through <see cref="AnalysisStateCodec"/>, and
/// <see cref="TrackIndexMapping.FromStoredBlob"/> using it instead of stamping the import moment.
/// </summary>
public sealed class TrackIndexMappingTests
{
    private static TrackMetadata Meta(TrackAnalysisState? stored) => new()
    {
        FallbackName = "track",
        StoredAnalysis = stored,
    };

    private static TrackAnalysisState State(DateTime? analysedAtUtc) => new()
    {
        Version = TrackAnalysisState.CurrentVersion,
        AnalysedAtUtc = analysedAtUtc,
        Detected = new AnalysisResult { Bpm = 128, Energy = 6 },
        BpmDecision = FieldDecision.Applied,
        KeyDecision = FieldDecision.Applied,
        EnergyDecision = FieldDecision.Applied,
    };

    // -- TrackAnalysisState.AnalysedAtUtc round-trips through the codec ---------

    [Fact]
    public void Codec_round_trips_AnalysedAtUtc_to_the_second()
    {
        var stamp = new DateTime(2026, 7, 15, 9, 41, 7, DateTimeKind.Utc);
        var state = State(stamp);

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(stamp, restored!.AnalysedAtUtc);
        Assert.Equal(DateTimeKind.Utc, restored.AnalysedAtUtc!.Value.Kind);
    }

    [Fact]
    public void Codec_round_trips_null_AnalysedAtUtc()
    {
        var state = State(null);

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Null(restored!.AnalysedAtUtc);
    }

    [Fact]
    public void An_old_blob_with_no_aat_field_still_loads_with_AnalysedAtUtc_null()
    {
        // Verbatim shape of a blob written before this field existed: no "aat" key at all.
        // Every other field is real so this also proves the new field didn't disturb parsing.
        const string oldBlob = """
            {"v":9,"bpm":128.0,"kpc":9,"kmd":"min","kcf":0.8,"nrg":6,"abpm":"A","akey":"A","anrg":"A"}
            """;

        var restored = AnalysisStateCodec.TryParse(oldBlob);

        Assert.NotNull(restored);
        Assert.Null(restored!.AnalysedAtUtc);
        Assert.Equal(9, restored.Version);
        Assert.Equal(128.0, restored.Detected.Bpm);
        Assert.Equal(6, restored.Detected.Energy);
    }

    // -- TrackIndexMapping.FromStoredBlob uses it --------------------------------

    [Fact]
    public void FromStoredBlob_records_the_blobs_own_analysedAt_when_present()
    {
        var stamp = new DateTime(2026, 6, 3, 8, 0, 0, DateTimeKind.Utc);
        var meta  = Meta(State(stamp));

        var entry = TrackIndexMapping.FromStoredBlob(
            @"C:\music\track.flac", 1_000_000, DateTime.UtcNow, meta);

        Assert.NotNull(entry);
        Assert.Equal(TrackIndexEntry.FormatUtc(stamp), entry!.AnalysedAt);
    }

    [Fact]
    public void FromStoredBlob_falls_back_to_the_unknown_sentinel_not_to_now()
    {
        // The blob is real (v9, fully applied) but carries no AnalysedAtUtc - exactly the shape
        // of every blob written before this field existed. Before the L7 fix this stamped
        // DateTime.UtcNow (the import moment); that is the bug the night report measured.
        var before = DateTime.UtcNow;
        var meta   = Meta(State(analysedAtUtc: null));

        var entry = TrackIndexMapping.FromStoredBlob(
            @"C:\music\track.flac", 1_000_000, DateTime.UtcNow, meta);
        var after = DateTime.UtcNow;

        Assert.NotNull(entry);
        Assert.Equal(TrackIndexEntry.FormatUtc(TrackIndexEntry.UnknownAnalysedAt), entry!.AnalysedAt);

        // Explicitly rule out the old behaviour: the stamp must NOT fall in the "now" window a
        // reader-time fallback would have produced.
        var parsed = DateTime.Parse(entry.AnalysedAt, null,
            System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        Assert.True(parsed < before, "unknown analysed-at must not look like a fresh import stamp");
        Assert.True(parsed < after);
    }

    [Fact]
    public void FromStoredBlob_on_a_verbatim_old_blob_still_loads_and_is_conservatively_unknown()
    {
        // A pre-L7 blob round-tripped through the (unchanged) parser: no "aat" key at all.
        const string oldBlobJson = """
            {"v":9,"bpm":140.0,"kpc":2,"kmd":"maj","kcf":0.7,"nrg":8,"abpm":"A","akey":"A","anrg":"A"}
            """;
        var oldState = AnalysisStateCodec.TryParse(oldBlobJson);
        Assert.NotNull(oldState);

        var meta  = Meta(oldState);
        var entry = TrackIndexMapping.FromStoredBlob(
            @"C:\music\track.aiff", 2_000_000, DateTime.UtcNow, meta);

        Assert.NotNull(entry);
        // Old blob loads exactly as it does today: values intact, version intact.
        Assert.Equal(140.0, entry!.Bpm);
        Assert.Equal(9, entry.DetectionVersion);
        // But the analysed-at is honestly "unknown", not a fresh import stamp.
        Assert.Equal(TrackIndexEntry.FormatUtc(TrackIndexEntry.UnknownAnalysedAt), entry.AnalysedAt);
    }

    [Fact]
    public void ToIndexEntry_still_stamps_now_for_a_genuinely_fresh_DSP_run()
    {
        // A caller that just ran the DSP (not importing a blob) and doesn't pass analysedAtUtc
        // must keep getting "now" - that default is correct there; only FromStoredBlob's fallback
        // changed. Guards against fixing this by changing ToIndexEntry's own default instead.
        var before = DateTime.UtcNow;
        var entry = TrackIndexMapping.ToIndexEntry(
            @"C:\music\fresh.mp3", "hash", 1000, DateTime.UtcNow,
            meta: null,
            analysis: new AnalysisResult { Bpm = 120 },
            detectionVersion: TrackAnalysisState.CurrentVersion);
        var after = DateTime.UtcNow;

        var parsed = DateTime.Parse(entry.AnalysedAt, null,
            System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        Assert.InRange(parsed, before.AddSeconds(-1), after.AddSeconds(1));
    }
}
