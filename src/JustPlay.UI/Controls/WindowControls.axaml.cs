using Avalonia;
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
    /// <summary>Show the maximize button. Set False for fixed-size windows (e.g. JUST STREAM, whose
    /// layout isn't dynamic, so maximizing would only add dead space).</summary>
    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<WindowControls, bool>(nameof(ShowMaximize), defaultValue: true);

    public bool ShowMaximize
    {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

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
