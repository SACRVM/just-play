using JustPlay.Core.Organise;

namespace JustPlay.Core.Tests;

/// <summary>
/// Which files can reach a recycle bin, decided from the path. A file the shell cannot recycle is
/// not refused by the shell - it is deleted for good - so this answer has to be right BEFORE the
/// shell is asked. The drive type is injected: "Z:" is a mapped share and "E:" a USB stick here.
/// </summary>
public sealed class RecycleBinReachTests
{
    private static DriveType Drives(string root) => root switch
    {
        @"C:\" => DriveType.Fixed,
        @"Z:\" => DriveType.Network,
        @"E:\" => DriveType.Removable,
        @"R:\" => DriveType.CDRom,
        _      => DriveType.NoRootDirectory,
    };

    [Theory]
    [InlineData(@"\\nas\music\GENRES\a.mp3")]
    [InlineData(@"\\NAS\MUSIC\a.mp3")]
    [InlineData("//nas/music/a.mp3")]
    [InlineData(@"\\?\UNC\nas\music\a.mp3")]
    [InlineData(@"\\.\UNC\nas\music\a.mp3")]
    [InlineData(@"\\192.168.1.20\music\a.mp3")]
    [InlineData(@"Z:\GENRES\a.mp3")]            // a drive letter mapped to a share
    [InlineData(@"z:\GENRES\a.mp3")]
    [InlineData(@"\\?\Z:\GENRES\a.mp3")]
    public void A_network_share_has_no_bin(string path)
    {
        Assert.Equal(NoBinReason.NetworkShare, RecycleBinReach.OnWindows(path, Drives));
    }

    [Theory]
    [InlineData(@"E:\set\a.mp3")]
    [InlineData(@"\\?\E:\set\a.mp3")]
    public void A_removable_drive_has_no_bin(string path)
    {
        Assert.Equal(NoBinReason.RemovableDrive, RecycleBinReach.OnWindows(path, Drives));
    }

    [Theory]
    [InlineData(@"C:\music\a.mp3")]
    [InlineData(@"c:\music\a.mp3")]
    [InlineData("C:/music/a.mp3")]
    [InlineData(@"\\?\C:\music\a.mp3")]
    [InlineData(@"\\.\C:\music\a.mp3")]
    [InlineData(@"\\?\Volume{0b6a1f5e-0000-0000-0000-100000000000}\music\a.mp3")]
    [InlineData(@"R:\a.mp3")]                   // not a share or a stick - the shell decides
    [InlineData(@"Q:\a.mp3")]                   // a drive that is not there
    [InlineData("music/a.mp3")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Everything_else_is_left_to_the_shell(string? path)
    {
        Assert.Equal(NoBinReason.None, RecycleBinReach.OnWindows(path, Drives));
    }

    [Fact]
    public void A_drive_is_asked_about_by_its_root_and_a_share_is_not_asked_about_at_all()
    {
        var asked = new List<string>();
        DriveType Spy(string root) { asked.Add(root); return DriveType.Fixed; }

        RecycleBinReach.OnWindows(@"d:\music\a.mp3", Spy);
        RecycleBinReach.OnWindows(@"\\?\D:\music\a.mp3", Spy);
        RecycleBinReach.OnWindows(@"\\nas\music\a.mp3", Spy);

        Assert.Equal(new[] { @"D:\", @"D:\" }, asked);
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData(@"\\.\C:\x", false)]
    [InlineData(@"\\?\", false)]
    [InlineData(@"\\?\UNC", false)]
    [InlineData(@"C:\x", false)]
    [InlineData(@"\x", false)]
    public void Unc_means_a_server_not_a_device_prefix(string path, bool unc)
    {
        Assert.Equal(unc, RecycleBinReach.IsUnc(path));
    }

    [Fact]
    public void The_refusal_says_where_the_file_is_and_that_it_is_still_there()
    {
        var share = RecycleBinReach.Describe(NoBinReason.NetworkShare, "Recycle Bin");
        var stick = RecycleBinReach.Describe(NoBinReason.RemovableDrive, "Recycle Bin");

        Assert.Contains("network share", share, StringComparison.Ordinal);
        Assert.Contains("removable drive", stick, StringComparison.Ordinal);

        foreach (var text in new[] { share, stick, RecycleBinReach.Describe(NoBinReason.None, "") })
        {
            Assert.Contains("left where it is", text, StringComparison.Ordinal);
            Assert.DoesNotContain("deleted", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
