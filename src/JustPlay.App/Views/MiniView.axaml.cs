using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using JustPlay.App.Controls;

namespace JustPlay.App.Views;

public partial class MiniView : UserControl
{
    public MiniView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: false);
    }

    // Drag the borderless mini window from any non-interactive surface.
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && WindowChrome.IsInteractive(v)) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    // Window min/max/close now live in the shared WindowControls control.
}
