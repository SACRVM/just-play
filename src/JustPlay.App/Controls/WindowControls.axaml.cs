using Avalonia.Controls;
using Avalonia.Interactivity;

namespace JustPlay.App.Controls;

/// <summary>
/// Minimize / maximize / close caption buttons, shared by both chrome bars.
/// Resolves the hosting <see cref="Window"/> via <see cref="TopLevel"/> so it can
/// be dropped into either view without wiring.
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
        if (Window is { } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
