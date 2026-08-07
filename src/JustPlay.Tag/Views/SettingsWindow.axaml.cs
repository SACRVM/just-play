using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;

namespace JustPlay.Tag.Views;

/// <summary>
/// JUST TAG settings - a separate frameless card (like JUST STREAM's SettingsWindow), opened from the
/// main window's chrome gear. Theme picker + ID3 write mode; all state lives in the shared singleton
/// <see cref="ViewModels.SettingsViewModel"/>, so changes persist + reflect live.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY - re-setting it here trips
        // Avalonia's macOS opaque-fallback (black surround); see JustPlay MainWindow ctor.

        WindowPlacement.Track(this, "JustTag.Settings");
    }

    // Drag the frameless dialog from the chrome bar (but not from interactive controls) - shared predicate.
    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
