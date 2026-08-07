namespace JustPlay.UI.Controls;

/// <summary>
/// Platform flags for XAML (<c>{x:Static c:Platform.IsMac}</c>). The chrome layout is
/// platform-shaped, not theme-shaped: on macOS the caption buttons are traffic lights on
/// the LEFT of the chrome bar, on Windows squares on the RIGHT - both hand-drawn (no OS
/// chrome), so every JUST window places two <see cref="WindowControls"/> instances (one
/// per side) and lets these flags collapse the wrong one.
/// </summary>
public static class Platform
{
    public static bool IsMac { get; } = System.OperatingSystem.IsMacOS();
    public static bool IsNotMac { get; } = !System.OperatingSystem.IsMacOS();
}
