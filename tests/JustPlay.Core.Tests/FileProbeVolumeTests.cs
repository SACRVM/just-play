using System.IO;
using JustPlay.Core.Organise;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// The volume key decides whether a move is a rename or a verified copy. On Linux every path's
/// root is "/", so the key has to come from the mount table instead - or a move onto a NAS mount
/// would be taken for a rename and File.Move would copy-then-delete without the runner's check.
/// </summary>
public class FileProbeVolumeTests
{
    [LinuxOnlyFact("/proc is a mount of its own on Linux")]
    public void A_path_on_another_mount_is_another_volume()
    {
        var temp = SystemFileProbe.VolumeKey(Path.Combine(Path.GetTempPath(), "a.mp3"));

        Assert.Equal("/proc", SystemFileProbe.VolumeKey("/proc/self/status"));
        Assert.NotNull(temp);
        Assert.NotEqual("/proc", temp);
    }

    [LinuxOnlyFact("/proc is a mount of its own on Linux")]
    public void A_mount_point_does_not_claim_a_sibling_that_merely_starts_like_it()
    {
        // "/procfoo" is not under "/proc".
        Assert.NotEqual("/proc", SystemFileProbe.VolumeKey("/procfoo/a.mp3"));
    }

    [Fact]
    public void Two_files_in_one_folder_share_a_volume()
    {
        var dir = Path.GetTempPath();

        Assert.Equal(SystemFileProbe.VolumeKey(Path.Combine(dir, "a.mp3")),
                     SystemFileProbe.VolumeKey(Path.Combine(dir, "b.mp3")));
    }
}
