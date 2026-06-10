using System.Collections.Generic;
using System.IO;
using JustPlay.Cli.Commands;
using JustPlay.Cli.Index;
using JustPlay.Cli.Reports;

namespace JustPlay.Cli.Tests;

/// <summary>
/// Unit tests for the dedup logic and the sidecar index round-trip.
/// These are purely managed tests — no BASS, no audio decoding.
/// </summary>
public sealed class DedupTests : IDisposable
{
    private readonly string _tmpDir;

    public DedupTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"justplay_cli_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Exact-dupe detection
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExactDupes_TwoIdenticalFiles_FormsOneGroup()
    {
        // Arrange — two files with identical content
        var content = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAA, 0xBB };
        var a = WriteFile("a.mp3", content);
        var b = WriteFile("b.mp3", content);
        var c = WriteFile("c.mp3", [0xFF, 0xFE]); // different — should not appear

        // Act
        var groups = DedupCommand.FindExactDupes([a, b, c]);

        // Assert
        Assert.Single(groups);
        Assert.Equal(2, groups[0].FilePaths.Count);
        Assert.Contains(a, groups[0].FilePaths);
        Assert.Contains(b, groups[0].FilePaths);
        // Wasted bytes = 1 copy × file size
        Assert.Equal(content.Length, groups[0].WastedBytes);
    }

    [Fact]
    public void ExactDupes_ThreeIdenticalFiles_ReportsWastedAsTwoExtraCopies()
    {
        var content = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var a = WriteFile("x.mp3", content);
        var b = WriteFile("y.mp3", content);
        var c = WriteFile("z.mp3", content);

        var groups = DedupCommand.FindExactDupes([a, b, c]);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].FilePaths.Count);
        Assert.Equal(content.Length * 2L, groups[0].WastedBytes);
    }

    [Fact]
    public void ExactDupes_NoIdenticalFiles_ReturnsEmptyList()
    {
        var a = WriteFile("p.mp3", [0x01, 0x02]);
        var b = WriteFile("q.mp3", [0x03, 0x04]);
        var c = WriteFile("r.mp3", [0x05, 0x06]);

        var groups = DedupCommand.FindExactDupes([a, b, c]);

        Assert.Empty(groups);
    }

    [Fact]
    public void ExactDupes_SameSizeDifferentContent_NoGroup()
    {
        // Same size but different bytes → SHA-256 differs → not a dupe
        var a = WriteFile("s1.mp3", [0x01, 0x02, 0x03]);
        var b = WriteFile("s2.mp3", [0x04, 0x05, 0x06]);

        var groups = DedupCommand.FindExactDupes([a, b]);

        Assert.Empty(groups);
    }

    [Fact]
    public void ExactDupes_SingleFile_ReturnsEmptyList()
    {
        var a = WriteFile("solo.mp3", [0xAA, 0xBB]);

        var groups = DedupCommand.FindExactDupes([a]);

        Assert.Empty(groups);
    }

    [Fact]
    public void ExactDupes_EmptyInput_ReturnsEmptyList()
    {
        var groups = DedupCommand.FindExactDupes(Array.Empty<string>());
        Assert.Empty(groups);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Near-dupe detection (tag-based, no audio decode)
    // ─────────────────────────────────────────────────────────────────────────
    // Near-dedup reads real tags via TagLib#. We create minimal MP3s with matching
    // artist+title to exercise the grouping logic without needing BASS.

    [Fact]
    public void NearDupes_MatchingArtistTitleAndDuration_FormsOneGroup()
    {
        // Two MP3s with the same tagged artist+title and similar durations
        var a = WriteTaggedMp3("artist_a", "title_a");
        var b = WriteTaggedMp3("artist_a", "title_a");

        var groups = DedupCommand.FindNearDupes([a, b]);

        // Should find exactly one near-dupe group (duration both ~0s for minimal MP3s)
        Assert.Single(groups);
        Assert.Equal(2, groups[0].FilePaths.Count);
    }

    [Fact]
    public void NearDupes_DifferentArtists_NoGroup()
    {
        var a = WriteTaggedMp3("artist_x", "same_title");
        var b = WriteTaggedMp3("artist_y", "same_title");

        var groups = DedupCommand.FindNearDupes([a, b]);

        Assert.Empty(groups);
    }

    [Fact]
    public void NearDupes_SameArtistDifferentTitles_NoGroup()
    {
        var a = WriteTaggedMp3("same_artist", "title_1");
        var b = WriteTaggedMp3("same_artist", "title_2");

        var groups = DedupCommand.FindNearDupes([a, b]);

        Assert.Empty(groups);
    }

    [Fact]
    public void NearDupes_EmptyTags_NotGrouped()
    {
        // Files with no artist/title should be skipped by the near-dedup pass
        var a = WriteTaggedMp3("", "");
        var b = WriteTaggedMp3("", "");

        var groups = DedupCommand.FindNearDupes([a, b]);

        // Both have empty tags → near-dedup skips them (no useful key)
        Assert.Empty(groups);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sidecar index round-trip
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrackIndex_SaveAndLoad_RoundTrips()
    {
        var indexPath = Path.Combine(_tmpDir, "test.index.json");
        var entry = new TrackIndexEntry
        {
            FilePath         = @"C:\music\track01.mp3",
            ContentHash      = "abc123",
            AnalysedAt       = "2026-06-08T00:00:00Z",
            DetectionVersion = TrackIndex.CurrentDetectionVersion,
            FileSizeBytes    = 5_000_000L,
            Title            = "Test Track",
            Artist           = "Test Artist",
            DurationSec      = 240.5,
            Bpm              = 128.0,
            KeyName          = "A minor",
            KeyCamelot       = "8A",
            Energy           = 7,
            Success          = true,
        };

        var idx = new TrackIndex();
        idx.Entries[entry.FilePath] = entry;
        idx.Save(indexPath);

        var loaded = TrackIndex.Load(indexPath);

        Assert.True(loaded.Entries.ContainsKey(entry.FilePath));
        var e2 = loaded.Entries[entry.FilePath];
        Assert.Equal("Test Track",  e2.Title);
        Assert.Equal(128.0,         e2.Bpm);
        Assert.Equal("8A",          e2.KeyCamelot);
        Assert.Equal(7,             e2.Energy);
        Assert.Equal("abc123",      e2.ContentHash);
    }

    [Fact]
    public void TrackIndex_Load_MissingFile_ReturnsEmpty()
    {
        var nonExistent = Path.Combine(_tmpDir, "does_not_exist.json");
        var idx = TrackIndex.Load(nonExistent);
        Assert.Empty(idx.Entries);
    }

    [Fact]
    public void TrackIndex_IsUpToDate_CorrectHashAndVersion_ReturnsTrue()
    {
        var idx = new TrackIndex();
        idx.Entries[@"C:\test.mp3"] = new TrackIndexEntry
        {
            FilePath         = @"C:\test.mp3",
            ContentHash      = "deadbeef",
            AnalysedAt       = "2026-06-08T00:00:00Z",
            DetectionVersion = TrackIndex.CurrentDetectionVersion,
            FileSizeBytes    = 1000,
            DurationSec      = 0,
            Success          = true,
        };

        Assert.True(idx.IsUpToDate(@"C:\test.mp3", "deadbeef"));
    }

    [Fact]
    public void TrackIndex_IsUpToDate_WrongHash_ReturnsFalse()
    {
        var idx = new TrackIndex();
        idx.Entries[@"C:\test.mp3"] = new TrackIndexEntry
        {
            FilePath         = @"C:\test.mp3",
            ContentHash      = "oldHash",
            AnalysedAt       = "2026-06-08T00:00:00Z",
            DetectionVersion = TrackIndex.CurrentDetectionVersion,
            FileSizeBytes    = 1000,
            DurationSec      = 0,
            Success          = true,
        };

        Assert.False(idx.IsUpToDate(@"C:\test.mp3", "newHash"));
    }

    [Fact]
    public void TrackIndex_IsUpToDate_OldDetectionVersion_ReturnsFalse()
    {
        var idx = new TrackIndex();
        idx.Entries[@"C:\test.mp3"] = new TrackIndexEntry
        {
            FilePath         = @"C:\test.mp3",
            ContentHash      = "deadbeef",
            AnalysedAt       = "2026-06-08T00:00:00Z",
            DetectionVersion = TrackIndex.CurrentDetectionVersion - 1, // old version
            FileSizeBytes    = 1000,
            DurationSec      = 0,
            Success          = true,
        };

        // Old detection version → must re-analyse
        Assert.False(idx.IsUpToDate(@"C:\test.mp3", "deadbeef"));
    }

    [Fact]
    public void TrackIndex_IsUpToDate_MissingEntry_ReturnsFalse()
    {
        var idx = new TrackIndex();
        Assert.False(idx.IsUpToDate(@"C:\nonexistent.mp3", "anyhash"));
    }

    [Fact]
    public void TrackIndex_AtomicSave_TmpFileRemovedAfterSave()
    {
        var indexPath = Path.Combine(_tmpDir, "atomic.json");
        var tmpPath   = indexPath + ".tmp";

        var idx = new TrackIndex();
        idx.Save(indexPath);

        Assert.True(File.Exists(indexPath),  "Index file should exist after save.");
        Assert.False(File.Exists(tmpPath), ".tmp file should be removed after save.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AudioFiles helpers
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sha256_SameContent_ProducesSameHash()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var f1 = WriteFile("h1.mp3", content);
        var f2 = WriteFile("h2.mp3", content);

        Assert.Equal(AudioFiles.Sha256(f1), AudioFiles.Sha256(f2));
    }

    [Fact]
    public void Sha256_DifferentContent_ProducesDifferentHash()
    {
        var f1 = WriteFile("d1.mp3", [0x01, 0x02]);
        var f2 = WriteFile("d2.mp3", [0x03, 0x04]);

        Assert.NotEqual(AudioFiles.Sha256(f1), AudioFiles.Sha256(f2));
    }

    [Fact]
    public void Enumerate_OnlyReturnsAudioExtensions()
    {
        var mp3 = WriteFile("song.mp3", [0x01]);
        var txt = WriteFile("readme.txt", [0x02]);
        var flac = WriteFile("track.flac", [0x03]);

        var found = AudioFiles.Enumerate(_tmpDir).ToList();

        Assert.Contains(mp3,  found);
        Assert.Contains(flac, found);
        Assert.DoesNotContain(txt, found);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// Creates a minimal TagLib#-tagged MP3 in the temp dir with the given artist+title.
    /// Duration will be ~0 for these tiny synthetic files.
    /// </summary>
    private string WriteTaggedMp3(string artist, string title)
    {
        var path = Path.Combine(_tmpDir, $"tagged_{Guid.NewGuid():N}.mp3");
        // Minimal ID3v2.3 header (10 bytes) + one silent MPEG frame (4 bytes)
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = 3; // version 2.3
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        File.WriteAllBytes(path, bare);

        using var f = TagLib.File.Create(path);
        f.Tag.Performers = [artist];
        f.Tag.Title      = title;
        f.Save();

        return path;
    }
}
