using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JustPlay.App.ViewModels;

namespace JustPlay.App.Views;

public partial class MaxView : UserControl
{
    public MaxView()
    {
        InitializeComponent();
        // Drag the frameless window by clicking the chrome bar.
        this.FindControl<Border>("ChromeBar")?.AddHandler(PointerPressedEvent, OnChromePressed, RoutingStrategies.Tunnel);
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && IsInteractive(v)) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    private static bool IsInteractive(Visual? v)
    {
        while (v is not null)
        {
            if (v is Button or RangeBase) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm
            && this.FindControl<ListBox>("TrackList")?.SelectedItem is TrackViewModel track)
        {
            vm.PlayTrackCommand.Execute(track);
        }
    }

    // ── Tab swap (UP NEXT / LYRICS) ────────────────────────────────────────
    private void OnUpNextTabClick(object? sender, RoutedEventArgs e) => ShowTab(queue: true);
    private void OnLyricsTabClick(object? sender, RoutedEventArgs e) => ShowTab(queue: false);


    private void ShowTab(bool queue)
    {
        var qp = this.FindControl<Panel>("QueuePanel");
        var lp = this.FindControl<StackPanel>("LyricsPanel");
        var qt = this.FindControl<Button>("UpNextTab");
        var lt = this.FindControl<Button>("LyricsTab");
        if (qp is not null) qp.IsVisible = queue;
        if (lp is not null) lp.IsVisible = !queue;
        qt?.Classes.Set("active", queue);
        lt?.Classes.Set("active", !queue);
    }

    // ── Traffic-light window controls ──────────────────────────────────────
    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window w) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
