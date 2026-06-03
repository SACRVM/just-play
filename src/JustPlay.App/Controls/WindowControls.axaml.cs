using Avalonia.Controls;
using Avalonia.Interactivity;
using JustPlay.App.Views;

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
        // The shell window does a custom maximize (work-area fill) because it's a borderless
        // transparent window with no OS maximize; fall back to WindowState elsewhere.
        if (Window is MainWindow mw)
            mw.ToggleMaximize();
        else if (Window is { } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
