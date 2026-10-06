namespace JustPlay.Core.Layout;

/// <summary>What the operating system says a window is doing. The same four states as Avalonia's
/// <c>WindowState</c>, restated here so the maximize bookkeeping below can be unit-tested without a
/// window.</summary>
public enum OsWindowState
{
    Normal,
    Minimized,
    Maximized,
    FullScreen,
}

/// <summary>
/// Whether a frameless JUST window counts as maximized - which it can be in two independent ways, and
/// mixing them up is how the flag got lost.
///
/// <para><b>Custom</b>: the suite's own work-area fill (<c>FramelessMaximize.Toggle</c>). The OS knows
/// nothing about it - the window state stays Normal the whole time, so no window-state change may ever
/// clear it. <b>OS</b>: Windows' own maximize (Win+Up, a snap to the top edge, the taskbar menu), which
/// the window state does report.</para>
///
/// <para>The bug this exists for: every window-state change used to recompute the flag from the window
/// state alone. Maximize (custom), minimize, restore - and the restore reported Normal, which wiped
/// the flag while the window still filled the screen. It came back with rounded corners, the shadow
/// margin and the grips, and the caption button offered "maximize"; pressing it then recorded the
/// screen-filling bounds as the size to restore to. Minimized says nothing about what the window is
/// when it comes back, so it is ignored, and only the OS half follows the window state.</para>
///
/// <para>Platform-agnostic (JustPlay.Core, no Avalonia) so the state machine is tested, not eyeballed -
/// the same reason <see cref="ColumnWidthFitter"/> lives here.</para>
/// </summary>
public sealed class WindowMaximizeState
{
    /// <summary>The suite's own work-area fill is active.</summary>
    public bool IsCustomMaximized { get; private set; }

    /// <summary>The OS reports the window maximized or full screen.</summary>
    public bool IsOsMaximized { get; private set; }

    /// <summary>Either kind: the card is squared off, the grips are hidden, and the caption button
    /// offers "restore".</summary>
    public bool IsMaximized => IsCustomMaximized || IsOsMaximized;

    /// <summary>The custom maximize filled the work area.</summary>
    public void EnterCustom() => IsCustomMaximized = true;

    /// <summary>The custom maximize put the window back to its bounds from before.</summary>
    public void LeaveCustom() => IsCustomMaximized = false;

    /// <summary>
    /// The OS reported a new window state. Minimized changes nothing: a window coming back from the
    /// taskbar returns to whatever it was, and the state it returns to is reported then. Every other
    /// state settles the OS half - and only the OS half; the custom maximize never shows up in the
    /// window state, so the window state can never end it.
    /// </summary>
    public void OnOsStateChanged(OsWindowState state)
    {
        if (state == OsWindowState.Minimized) return;
        IsOsMaximized = state is OsWindowState.Maximized or OsWindowState.FullScreen;
    }
}
