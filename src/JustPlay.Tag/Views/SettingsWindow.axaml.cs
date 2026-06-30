using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.UI.Controls;

namespace JustPlay.Tag.Views;

/// <summary>
/// JUST TAG settings — a separate frameless card (like JUST STREAM's SettingsWindow), opened from the
/// main window's chrome gear. Theme picker + ID3 write mode; all state lives in the shared singleton
/// <see cref="ViewModels.SettingsViewModel"/>, so changes persist + reflect live.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Belt-and-braces transparency (the XAML IReadOnlyList TypeConverter isn't honoured on every minor version).
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.Blur,
        };
    }

    // Drag the frameless dialog from the chrome bar (but not from interactive controls) — shared predicate.
    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
