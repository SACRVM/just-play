using System.IO;

namespace JustPlay.Library.Tests;

/// <summary>
/// The numbers that decide whether a folder needs looking at: how many tracks it holds, the newest
/// modification time among them, and a hash of the listing.
/// </summary>
public sealed class FolderFingerprintTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jp-fp-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderFingerprintTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime T = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_count_and_time_matches()
    {
        var a = new FolderFingerprint(12, T);
        Assert.True(a.Matches(new FolderFingerprint(12, T)));
    }

    [Fact]
    public void A_different_count_never_matches()
    {
        var a = new FolderFingerprint(12, T);
        Assert.False(a.Matches(new FolderFingerprint(13, T)));
    }

    [Theory]
    [InlineData(1.9,  true)]    // filesystem rounding, not an edit
    [InlineData(-1.9, true)]
    [InlineData(60.0, false)]   // a retag
    public void The_newest_time_carries_the_same_two_second_tolerance(double deltaSeconds, bool expected)
    {
        var a = new FolderFingerprint(12, T);
        Assert.Equal(expected, a.Matches(new FolderFingerprint(12, T.AddSeconds(deltaSeconds))));
    }

    [Fact]
    public void Two_empty_folders_match_and_an_empty_one_never_matches_a_full_one()
    {
        Assert.True(FolderFingerprint.None.Matches(new FolderFingerprint(0, null)));
        Assert.False(FolderFingerprint.None.Matches(new FolderFingerprint(0, T)));
    }

    [Fact]
    public void FromDisk_counts_audio_only_and_takes_the_newest()
    {
        File.WriteAllText(Path.Combine(_root, "old.mp3"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "old.mp3"), T);

        File.WriteAllText(Path.Combine(_root, "new.flac"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "new.flac"), T.AddHours(3));

        File.WriteAllText(Path.Combine(_root, "cover.jpg"), "x");   // not audio
        File.SetLastWriteTimeUtc(Path.Combine(_root, "cover.jpg"), T.AddDays(9));

        var fp = FolderFingerprint.FromDisk(_root);

        Assert.Equal(2, fp.FileCount);
        Assert.Equal(T.AddHours(3), fp.MaxModifiedUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void FromDisk_ignores_subfolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Sub"));
        File.WriteAllText(Path.Combine(_root, "here.mp3"), "x");
        File.WriteAllText(Path.Combine(_root, "Sub", "below.mp3"), "x");

        Assert.Equal(1, FolderFingerprint.FromDisk(_root).FileCount);
    }

    [Fact]
    public void An_empty_folder_has_no_newest_time()
    {
        var fp = FolderFingerprint.FromDisk(_root);

        Assert.Equal(0, fp.FileCount);
        Assert.Null(fp.MaxModifiedUtc);
    }

    // -- The listing hash ------------------------------------------------------------------------

    private static AudioFiles.ScannedFile Scanned(string name, long size, DateTime modifiedUtc) =>
        new(Path.Combine(@"C:\m", name), size, modifiedUtc);

    [Fact]
    public void A_fingerprint_stored_without_a_hash_never_matches_one_with_a_hash()
    {
        // What an older build stored. It must cost one extra per-file check, never a false match.
        var stored = new FolderFingerprint(2, T);
        var live   = new FolderFingerprint(2, T, ListingHash: 12345);

        Assert.False(stored.Matches(live));
        Assert.False(live.Matches(stored));
        Assert.True(live.Matches(live with { }));
    }

    [Fact]
    public void A_rename_moves_the_hash_though_count_and_newest_time_stay()
    {
        var before = FolderFingerprint.FromFiles(
            [Scanned("a.mp3", 100, T), Scanned("old-name.mp3", 200, T.AddDays(-3))]);
        var after = FolderFingerprint.FromFiles(
            [Scanned("a.mp3", 100, T), Scanned("new-name.mp3", 200, T.AddDays(-3))]);

        Assert.Equal(before.FileCount, after.FileCount);
        Assert.Equal(before.MaxModifiedUtc, after.MaxModifiedUtc);
        Assert.False(before.Matches(after));
    }

    [Fact]
    public void A_file_overwritten_by_an_older_copy_moves_the_hash()
    {
        // An Explorer copy keeps the SOURCE mtime - older than the folder's newest, so that one stays.
        var before = FolderFingerprint.FromFiles(
            [Scanned("a.mp3", 100, T), Scanned("b.mp3", 200, T.AddDays(-3))]);
        var after = FolderFingerprint.FromFiles(
            [Scanned("a.mp3", 100, T), Scanned("b.mp3", 999, T.AddDays(-30))]);

        Assert.False(before.Matches(after));
    }

    [Fact]
    public void The_hash_ignores_listing_order_and_name_case()
    {
        // Shares list in whatever order they like, and the index compares paths case-insensitively.
        var one = FolderFingerprint.FromFiles(
            [Scanned("a.mp3", 100, T), Scanned("b.mp3", 200, T.AddDays(-3))]);
        var two = FolderFingerprint.FromFiles(
            [Scanned("B.MP3", 200, T.AddDays(-3)), Scanned("a.mp3", 100, T)]);

        Assert.Equal(one.ListingHash, two.ListingHash);
        Assert.True(one.Matches(two));
    }

    [Fact]
    public void FromDisk_throws_for_a_folder_it_cannot_list_instead_of_calling_it_empty()
    {
        Assert.ThrowsAny<IOException>(() => FolderFingerprint.FromDisk(Path.Combine(_root, "gone")));
    }
}
