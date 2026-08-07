using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Reactive;

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

    /// <summary>Show the minimize button. Set False for dialogs (About, Log, Settings) - on
    /// Windows the square disappears (dialogs carry only the x), on macOS the yellow dot stays
    /// visible but greyed/disabled, like a native mac dialog's traffic lights.</summary>
    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<WindowControls, bool>(nameof(ShowMinimize), defaultValue: true);

    public bool ShowMinimize
    {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    /// <summary>
    /// True while the hosting window is maximized - mirrored from
    /// <see cref="Behaviors.FramelessMaximize.IsMaximizedProperty"/> on the window.
    ///
    /// <para>The maximize button swaps its mark on this: a single square means "maximize", two
    /// overlapping rectangles mean "restore". We drew the square in both states, which told a
    /// maximized window's user to maximize it again (Chloe 2026-08-06).</para>
    /// </summary>
    public static readonly StyledProperty<bool> IsMaximizedProperty =
        AvaloniaProperty.Register<WindowControls, bool>(nameof(IsMaximized));

    public bool IsMaximized
    {
        get => GetValue(IsMaximizedProperty);
        set => SetValue(IsMaximizedProperty, value);
    }

    /// <summary>Tooltip for the maximize button - "Restore" once it restores.</summary>
    public static readonly StyledProperty<string> MaximizeTipProperty =
        AvaloniaProperty.Register<WindowControls, string>(nameof(MaximizeTip), defaultValue: "Maximize");

    public string MaximizeTip
    {
        get => GetValue(MaximizeTipProperty);
        set => SetValue(MaximizeTipProperty, value);
    }

    public WindowControls() => InitializeComponent();

    private Window? Window => TopLevel.GetTopLevel(this) as Window;

    private IDisposable? _maximizedSubscription;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The window is only reachable once we are in the tree - and the custom maximize never
        // touches WindowState, so the attached flag is the only thing there is to watch.
        if (Window is { } window)
            _maximizedSubscription = window
                .GetObservable(Behaviors.FramelessMaximize.IsMaximizedProperty)
                .Subscribe(new AnonymousObserver<bool>(maximized =>
                {
                    IsMaximized = maximized;
                    MaximizeTip = maximized ? "Restore" : "Maximize";
                }));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _maximizedSubscription?.Dispose();
        _maximizedSubscription = null;
        base.OnDetachedFromVisualTree(e);
    }

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
