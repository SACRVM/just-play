using JustPlay.Core.Layout;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// The frameless windows' maximize flag. The custom maximize never shows up in the window state, so
/// no window-state change may end it; the OS maximize does, and only it follows the window state.
/// </summary>
public class WindowMaximizeStateTests
{
    [Fact]
    public void Custom_maximize_survives_minimize_and_restore()
    {
        // The regression: the restore reported Normal and that alone used to clear the flag, while
        // the window still filled the screen.
        var s = new WindowMaximizeState();
        s.EnterCustom();

        s.OnOsStateChanged(OsWindowState.Minimized);
        Assert.True(s.IsMaximized);

        s.OnOsStateChanged(OsWindowState.Normal);
        Assert.True(s.IsMaximized);
        Assert.True(s.IsCustomMaximized);
    }

    [Fact]
    public void Os_maximize_ends_with_the_os_restore()
    {
        var s = new WindowMaximizeState();

        s.OnOsStateChanged(OsWindowState.Maximized);
        Assert.True(s.IsMaximized);

        s.OnOsStateChanged(OsWindowState.Normal);
        Assert.False(s.IsMaximized);
    }

    [Fact]
    public void Minimize_says_nothing_about_an_os_maximize()
    {
        // Windows brings a maximized window back maximized; in between, nothing may flip.
        var s = new WindowMaximizeState();
        s.OnOsStateChanged(OsWindowState.Maximized);

        s.OnOsStateChanged(OsWindowState.Minimized);
        Assert.True(s.IsMaximized);

        s.OnOsStateChanged(OsWindowState.Maximized);
        Assert.True(s.IsMaximized);
    }

    [Fact]
    public void Coming_back_from_minimized_as_normal_ends_an_os_maximize()
    {
        // The OS put it back at its normal bounds - so it is not maximized any more.
        var s = new WindowMaximizeState();
        s.OnOsStateChanged(OsWindowState.Maximized);
        s.OnOsStateChanged(OsWindowState.Minimized);

        s.OnOsStateChanged(OsWindowState.Normal);

        Assert.False(s.IsMaximized);
    }

    [Fact]
    public void Os_restore_does_not_end_a_custom_maximize_underneath()
    {
        // Custom-maximized, then Win+Up, then Win+Down: Windows restores to the bounds it saw before
        // its own maximize - the work area - so the window still fills the screen.
        var s = new WindowMaximizeState();
        s.EnterCustom();
        s.OnOsStateChanged(OsWindowState.Maximized);

        s.OnOsStateChanged(OsWindowState.Normal);

        Assert.True(s.IsMaximized);
        Assert.False(s.IsOsMaximized);
    }

    [Fact]
    public void Full_screen_counts_as_maximized()
    {
        var s = new WindowMaximizeState();

        s.OnOsStateChanged(OsWindowState.FullScreen);

        Assert.True(s.IsMaximized);
    }

    [Fact]
    public void Leaving_the_custom_maximize_clears_it()
    {
        var s = new WindowMaximizeState();
        s.EnterCustom();

        s.LeaveCustom();

        Assert.False(s.IsMaximized);
    }
}
