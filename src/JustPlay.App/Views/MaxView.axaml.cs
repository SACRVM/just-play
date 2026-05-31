using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.App.Controls;
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
        if (e.Source is Visual v && WindowChrome.IsInteractive(v)) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
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

    // Window min/max/close now live in the shared WindowControls control.
}
