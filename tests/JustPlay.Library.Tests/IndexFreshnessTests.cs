using System.IO;

namespace JustPlay.Library.Tests;

/// <summary>
/// The two things 0.6 changes about index freshness:
/// the CHEAP key (size + mtime, so a rescan does not pull the whole library over SMB), and
/// STALENESS AS RULES (so the FLAC mono-decode debt can be paid without re-analysing every MP3).
/// Plus the non-negotiable: index files written before 0.6 still load.
/// </summary>
public sealed class IndexFreshnessTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "jp-idx-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public IndexFreshnessTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime Mtime = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static TrackIndexEntry Entry(
        string path = @"C:\music\track.mp3",
        long size = 8_000_000,
        DateTime? modified = null,
        string analysedAt = "2026-07-20T12:00:00.0000000Z",
        bool success = true) =>
        new()
        {
            FilePath         = path,
            ContentHash      = "deadbeef",
            AnalysedAt       = analysedAt,
            DetectionVersion = TrackIndex.CurrentDetectionVersion,
            FileSizeBytes    = size,
            ModifiedUtc      = modified is null ? null : TrackIndexEntry.FormatUtc(modified.Value),
            Success          = success,
        };

    // -- Cheap key -------------------------------------------------------------

    [Fact]
    public void LooksUnchanged_true_for_same_size_and_mtime()
    {
        var entry = Entry(modified: Mtime);
        Assert.True(entry.LooksUnchanged(8_000_000, Mtime));
    }

    [Fact]
    public void LooksUnchanged_false_when_size_differs()
    {
        var entry = Entry(modified: Mtime);
        Assert.False(entry.LooksUnchanged(8_000_001, Mtime));
    }

    [Theory]
    [InlineData(0.0,  true)]
    [InlineData(1.9,  true)]   // FAT/exFAT round to 2 s; SMB clients drift a hair
    [InlineData(-1.9, true)]
    [InlineData(3.0,  false)]  // a real edit
    public void LooksUnchanged_tolerates_two_seconds_of_mtime_drift(double deltaSeconds, bool expected)
    {
        var entry = Entry(modified: Mtime);
        Assert.Equal(expected, entry.LooksUnchanged(8_000_000, Mtime.AddSeconds(deltaSeconds)));
    }

    [Fact]
    public void LooksUnchanged_false_when_the_entry_predates_0_6()
    {
        // No mtime recorded -> we cannot answer cheaply and must fall back to the hash.
        var legacy = Entry(modified: null);
        Assert.False(legacy.LooksUnchanged(8_000_000, Mtime));
    }

    [Fact]
    public void AdoptedWith_makes_a_legacy_entry_cheap_without_rehashing()
    {
        var legacy   = Entry(modified: null);
        var adopted  = legacy.AdoptedWith(Mtime);

        Assert.True(adopted.LooksUnchanged(8_000_000, Mtime));
        // Adoption records the mtime and changes NOTHING about the analysis it is trusting.
        Assert.Equal(legacy.ContentHash, adopted.ContentHash);
        Assert.Equal(legacy.AnalysedAt,  adopted.AnalysedAt);
    }

    // -- Staleness rules -------------------------------------------------------

    [Fact]
    public void TrustEverything_never_reports_a_reason()
    {
        var policy = StalenessPolicy.TrustEverything;
        Assert.Null(policy.StaleReason(Entry(success: false)));
    }

    [Fact]
    public void Failed_rule_catches_a_failed_analysis_but_not_an_unanalysed_file()
    {
        var policy = new StalenessPolicy().With(StaleRule.Failed());
        var failed = Entry(success: false) with { Error = "decode error" };

        Assert.Equal("analysis failed", policy.StaleReason(failed));
        Assert.Null(policy.StaleReason(Entry()));
        // Success=false + Error=null means "never analysed" - reporting that as a FAILURE would
        // send the reader hunting for a bug that is not there.
        Assert.Null(policy.StaleReason(Entry(success: false)));
    }

    [Fact]
    public void NeverAnalysed_rule_catches_the_not_yet_processed_file()
    {
        var policy = new StalenessPolicy().With(StaleRule.NeverAnalysed());

        Assert.Equal("never analysed", policy.StaleReason(Entry(success: false)));
        Assert.Null(policy.StaleReason(Entry(success: false) with { Error = "decode error" }));
        Assert.Null(policy.StaleReason(Entry()));
    }

    [Theory]
    // Her real library index was written 2026-06-12, before the fix in c687d46 (2026-07-10).
    [InlineData(@"C:\music\track.flac", "2026-06-12T17:53:00.0000000Z", true)]
    [InlineData(@"C:\music\track.FLAC", "2026-06-12T17:53:00.0000000Z", true)]  // case-insensitive
    [InlineData(@"C:\music\track.mp3",  "2026-06-12T17:53:00.0000000Z", false)] // MP3s were fine
    [InlineData(@"C:\music\track.flac", "2026-07-20T10:00:00.0000000Z", false)] // after the fix
    [InlineData(@"C:\music\track.flac", "not-a-timestamp",              true)]  // unknown -> redo
    // c687d46's own message: the bug hit WAV and AIFF too, not just FLAC (L6, 2026-08-01).
    [InlineData(@"C:\music\track.wav",  "2026-06-12T17:53:00.0000000Z", true)]
    [InlineData(@"C:\music\track.aiff", "2026-06-12T17:53:00.0000000Z", true)]
    [InlineData(@"C:\music\track.AIFF", "2026-06-12T17:53:00.0000000Z", true)]  // case-insensitive
    [InlineData(@"C:\music\track.aif",  "2026-06-12T17:53:00.0000000Z", true)]
    [InlineData(@"C:\music\track.wav",  "2026-07-20T10:00:00.0000000Z", false)] // after the fix
    [InlineData(@"C:\music\track.aiff", "2026-07-20T10:00:00.0000000Z", false)] // after the fix
    [InlineData(@"C:\music\track.aif",  "2026-07-20T10:00:00.0000000Z", false)] // after the fix
    public void FlacMonoDecodeBug_targets_only_the_affected_entries(
        string path, string analysedAt, bool expectedStale)
    {
        var policy = new StalenessPolicy().With(StaleRule.FlacMonoDecodeBug());
        var reason = policy.StaleReason(Entry(path: path, analysedAt: analysedAt));

        Assert.Equal(expectedStale, reason is not null);
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData(".wav")]
    [InlineData(".aiff")]
    [InlineData(".aif")]
    public void FlacMonoDecodeBug_treats_unknown_analysedAt_as_stale_not_clean(string extension)
    {
        // TrackIndexMapping.FromStoredBlob stamps TrackIndexEntry.UnknownAnalysedAt (the Unix
        // epoch) when a blob carries no analysed-at of its own - the L6 fix. It must land on the
        // "stale" side of the rule, never the "clean" side: the honest answer to "we don't know
        // when this ran" is "assume the worst", not "assume it's fine".
        var policy = new StalenessPolicy().With(StaleRule.FlacMonoDecodeBug());
        var entry = Entry(
            path: $@"C:\music\track{extension}",
            analysedAt: TrackIndexEntry.FormatUtc(TrackIndexEntry.UnknownAnalysedAt));

        Assert.NotNull(policy.StaleReason(entry));
    }

    [Fact]
    public void CountByReason_groups_what_the_library_panel_shows()
    {
        var index = new TrackIndex();
        index.Entries[@"C:\a.flac"] = Entry(@"C:\a.flac", analysedAt: "2026-06-12T00:00:00.0000000Z");
        index.Entries[@"C:\b.flac"] = Entry(@"C:\b.flac", analysedAt: "2026-06-12T00:00:00.0000000Z");
        index.Entries[@"C:\c.mp3"]  = Entry(@"C:\c.mp3",  success: false) with { Error = "decode error" };
        index.Entries[@"C:\d.mp3"]  = Entry(@"C:\d.mp3");

        var policy = new StalenessPolicy()
            .With(StaleRule.Failed())
            .With(StaleRule.FlacMonoDecodeBug());

        var counts = policy.CountByReason(index);

        Assert.Equal(2, counts["FLAC/WAV/AIFF mono-decode fix (c687d46)"]);
        Assert.Equal(1, counts["analysis failed"]);
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public void First_matching_rule_wins_so_the_reason_is_deterministic()
    {
        var policy = new StalenessPolicy()
            .With(StaleRule.Failed())
            .With(StaleRule.FlacMonoDecodeBug());

        // Both rules match; the reported reason is the first rule's.
        var entry = Entry(@"C:\x.flac", analysedAt: "2026-06-01T00:00:00.0000000Z", success: false)
            with { Error = "decode error" };
        Assert.Equal("analysis failed", policy.StaleReason(entry));
    }

    // -- Schema compatibility (non-negotiable) ---------------------------------

    [Fact]
    public void SaveAndLoad_round_trips_through_the_library_context()
    {
        var path  = Path.Combine(_tmp, "index.json");
        var index = new TrackIndex();
        index.Entries[@"C:\music\track.mp3"] = Entry(modified: Mtime) with
        {
            Bpm = 145.0, KeyCamelot = "8A", Energy = 8, GridConfidence = 0.72,
        };
        index.Save(path);

        var loaded = TrackIndex.Load(path);
        var entry  = loaded.Entries[@"C:\music\track.mp3"];

        Assert.Equal(145.0, entry.Bpm);
        Assert.Equal("8A",  entry.KeyCamelot);
        Assert.Equal(8,     entry.Energy);
        Assert.Equal(0.72,  entry.GridConfidence);
        Assert.True(entry.LooksUnchanged(8_000_000, Mtime));
        Assert.False(File.Exists(path + ".tmp"));   // atomic write leaves no debris
    }

    [Fact]
    public void Load_reads_a_pre_0_6_index_written_by_the_CLI()
    {
        // Verbatim shape of an entry from .claude/library-scan/sets.v9.index.json (2026-06-12):
        // camelCase names, detectionVersion 1, and NO modifiedUtc field.
        var path = Path.Combine(_tmp, "legacy.index.json");
        File.WriteAllText(path, """
        {
          "entries": {
            "\\\\nas\\music\\GENRES\\Techno\\x.flac": {
              "filePath": "\\\\nas\\music\\GENRES\\Techno\\x.flac",
              "contentHash": "abc123",
              "analysedAt": "2026-06-12T17:53:00.0000000Z",
              "detectionVersion": 1,
              "fileSizeBytes": 41234567,
              "title": "X",
              "durationSec": 372.5,
              "success": true,
              "bpm": 150.0,
              "keyCamelot": "11A",
              "energy": 9,
              "gridConfidence": 0.81
            }
          },
          "createdAt": "2026-06-12T17:00:00.0000000Z",
          "lastUpdatedAt": "2026-06-12T17:53:00.0000000Z"
        }
        """);

        var loaded = TrackIndex.Load(path);
        var entry  = Assert.Single(loaded.Entries).Value;

        Assert.Equal(150.0, entry.Bpm);
        Assert.Equal("11A", entry.KeyCamelot);
        Assert.Null(entry.ModifiedUtc);                       // pre-0.6: no cheap key yet
        Assert.True(entry.AdoptedWith(Mtime).LooksUnchanged(41234567, Mtime));

        // And it is exactly the entry the FLAC rule must catch.
        var policy = new StalenessPolicy().With(StaleRule.FlacMonoDecodeBug());
        Assert.NotNull(policy.StaleReason(entry));
    }

    [Fact]
    public void Load_of_a_missing_file_returns_an_empty_index()
    {
        var loaded = TrackIndex.Load(Path.Combine(_tmp, "nope.json"));
        Assert.Empty(loaded.Entries);
    }
}
