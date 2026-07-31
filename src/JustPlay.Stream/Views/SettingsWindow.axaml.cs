using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using JustPlay.Core.Storage;
using JustPlay.Stream.ViewModels;
using JustPlay.UI.Behaviors;

namespace JustPlay.Stream.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        WindowPlacement.Track(this, "JustStream.Settings");
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

    // Advanced tab → "SETTINGS & LOG FOLDER" right-click → open %LOCALAPPDATA%\JustStream in the OS
    // file manager (settings.json + crash logs live there). Same path JsonStreamSettingsService uses;
    // created if missing. Mirrors the REC button's "Open recordings folder".
    private void OnOpenSettingsFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = JustDataPaths.Combine("JustStream");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // never-crash rule: a missing drive / denied path must not take the settings window down.
            Console.WriteLine($"[Settings] Open settings folder failed: {ex.Message}");
        }
    }

    // Recording tab → FOLDER "Browse…": OS folder picker via StorageProvider. Click handler
    // (not a command) because the picker needs the Window's StorageProvider. Cancel = no change.
    private async void OnBrowseRecordingFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SettingsViewModel vm) return;

            // Start the picker where the setting currently POINTS (Chloe 2026-07-05). The
            // recordings folder itself is created lazily on the first record start, so it may
            // not exist yet — walk up to the nearest EXISTING ancestor (typically Music)
            // instead of letting the OS drop us at some unrelated last-used location.
            var start = (string?)vm.Stream.EffectiveRecordingFolder;
            while (!string.IsNullOrEmpty(start) && !Directory.Exists(start))
                start = Path.GetDirectoryName(start);

            var options = new FolderPickerOpenOptions
            {
                Title = "Choose recording folder",
                AllowMultiple = false,
            };
            if (!string.IsNullOrEmpty(start))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);

            var picks = await StorageProvider.OpenFolderPickerAsync(options);
            var folder = picks.Count > 0 ? picks[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(folder))
                vm.Stream.RecordingFolder = folder;
        }
        catch (Exception ex)
        {
            // never-crash rule: a failed picker must not take the settings window down.
            Console.WriteLine($"[Settings] Folder picker failed: {ex.Message}");
        }
    }
}
