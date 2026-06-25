using Avalonia.Controls;
using Avalonia.Interactivity;

namespace JustPlay.UI.Controls;

/// <summary>
/// Minimize / maximize / close caption buttons, shared by every JUST suite window.
/// Resolves the hosting <see cref="Window"/> via <see cref="TopLevel"/> and drives a
/// custom maximize through <see cref="IFramelessWindow"/>, so it needs no reference to
/// any app-specific Window type.
/// </summary>
public partial class WindowControls : UserControl
{
    public WindowControls() => InitializeComponent();

    private Window? Window => TopLevel.GetTopLevel(this) as Window;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Window?.Close();

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (Window is { } w) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        // Frameless windows do a custom work-area maximize (no OS maximize on a borderless
        // transparent window); fall back to WindowState for anything that isn't one.
        if (Window is IFramelessWindow fw)
            fw.ToggleMaximize();
        else if (Window is { } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
