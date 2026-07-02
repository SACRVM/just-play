using System.Linq;
using JustPlay.Core.Audio;
using Xunit;

namespace JustPlay.Core.Tests;

public class CaptureAppFilterTests
{
    private static RunningProcess Proc(int pid, string exe, bool window = true) => new(pid, exe, window);

    [Fact]
    public void DjApps_SortFirst_AndAreFlagged()
    {
        var apps = CaptureAppFilter.ToCaptureApps(new[]
        {
            Proc(10, "spotify"),
            Proc(20, "rekordbox"),
            Proc(30, "chrome"),
            Proc(40, "traktor"),
        });

        // DJ apps (rekordbox, traktor) come before media apps (chrome, spotify).
        Assert.Equal(new[] { true, true, false, false }, apps.Select(a => a.IsDjApp).ToArray());
        Assert.True(apps[0].IsDjApp && apps[1].IsDjApp);
        Assert.Contains(apps.Take(2), a => a.DisplayName == "rekordbox");
        Assert.Contains(apps.Take(2), a => a.DisplayName == "Traktor Pro");
    }

    [Fact]
    public void MediaApps_RankAboveOtherWindowedApps()
    {
        var apps = CaptureAppFilter.ToCaptureApps(new[]
        {
            Proc(10, "notepad"),   // unknown but windowed → "other"
            Proc(20, "spotify"),   // media
        });

        Assert.Equal("Spotify", apps[0].DisplayName);   // media before other
        Assert.Equal("Notepad", apps[1].DisplayName);
        Assert.All(apps, a => Assert.False(a.IsDjApp));
    }

    [Fact]
    public void BackgroundProcess_WithoutWindow_AndUnknown_IsDropped()
    {
        var apps = CaptureAppFilter.ToCaptureApps(new[]
        {
            Proc(10, "audiodg", window: false),   // system service, no window, unknown → drop
            Proc(20, "rekordbox", window: false), // DJ app qualifies even without a window
        });

        Assert.Single(apps);
        Assert.Equal("rekordbox", apps[0].DisplayName);
    }

    [Fact]
    public void DuplicateExecutables_CollapseToLowestPid()
    {
        var apps = CaptureAppFilter.ToCaptureApps(new[]
        {
            Proc(500, "chrome"),   // renderer child
            Proc(100, "chrome"),   // main
            Proc(300, "chrome"),
        });

        Assert.Single(apps);
        Assert.Equal(100, apps[0].ProcessId);
    }

    [Fact]
    public void ExecutableMatching_IsCaseInsensitive()
    {
        var apps = CaptureAppFilter.ToCaptureApps(new[] { Proc(1, "RekordBox") });
        Assert.Single(apps);
        Assert.True(apps[0].IsDjApp);
        Assert.Equal("rekordbox", apps[0].ExecutableName);  // normalised lower-case for persistence
    }

    [Fact]
    public void EmptyOrWhitespaceExecutables_AreIgnored()
    {
        var apps = CaptureAppFilter.ToCaptureApps(new[] { Proc(1, ""), Proc(2, "   "), Proc(3, "traktor") });
        Assert.Single(apps);
        Assert.Equal("Traktor Pro", apps[0].DisplayName);
    }

    [Fact]
    public void VersionedDjExecutable_IsRecognisedAndFriendlyNamed()
    {
        // Traktor's real process name carries the edition + version ("Traktor Pro 4") — the exact-match
        // lookup missed it, so Auto fell back to full-mix (cue bleed). Token matching fixes it.
        var apps = CaptureAppFilter.ToCaptureApps(new[] { Proc(1, "Traktor Pro 4") });
        Assert.Single(apps);
        Assert.True(apps[0].IsDjApp);
        Assert.Equal("Traktor Pro", apps[0].DisplayName);
    }

    [Theory]
    [InlineData("traktor", true)]
    [InlineData("Traktor Pro 4", true)]      // versioned / spaced real process name
    [InlineData("REKORDBOX", true)]
    [InlineData("rekordbox 7", true)]
    [InlineData("Serato DJ Pro", true)]
    [InlineData("VirtualDJ 2024", true)]
    [InlineData("djay Pro", true)]
    [InlineData("chrome", false)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    public void IsDjExecutable_Classifies(string exe, bool expected)
        => Assert.Equal(expected, CaptureAppFilter.IsDjExecutable(exe));
}
