using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.App.Controls;
using JustPlay.UI.Controls;
using JustPlay.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App.Views;

public partial class MiniView : UserControl
{
    public MiniView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: false);

        // macOS: caption buttons sit LEFT, so the expand button owns the top-right corner -
        // round its hover to the card radius (Button.cap.corner in JustStyles).
        if (System.OperatingSystem.IsMacOS())
            ExpandBtn.Classes.Add("corner");
    }

    // Drag the borderless mini window from any non-interactive surface.
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && WindowChrome.IsInteractive(v)) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    // Title-bar update badge -> show the update dialog, then install / ignore / dismiss.
    private async void OnUpdateBadge(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var http = JustPlay.App.Program.Services.GetRequiredService<System.Net.Http.HttpClient>();
        await JustPlay.App.Updates.UpdateFlow.ShowAndApplyAsync(owner, vm.Update, http);
    }

    // Window min/max/close now live in the shared WindowControls control.
}
