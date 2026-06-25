using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace JustPlay.Stream.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    // Drag the frameless dialog from the chrome bar (but not from interactive controls).
    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
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

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
