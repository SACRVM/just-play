using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JustPlay.Tag.ViewModels;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using JustPlay.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Tag.Views;

/// <summary>
/// JUST TAG main window — frameless floating-card shell shared with JUST PLAY / STREAM via the
/// JustPlay.UI design system (drag predicate = <see cref="WindowChrome"/>; About = the shared
/// <see cref="AboutWindow"/>). File pickers + drag-drop live here (they need the TopLevel) and
/// call into the <see cref="TagEditorViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY — re-setting it here trips
        // Avalonia's macOS opaque-fallback (black surround); see JustPlay MainWindow ctor.

        // Drag-drop an audio file onto the window to load it.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        WindowPlacement.Track(this, "JustTag.Main");
    }

    // Adaptive window: a bare drop-zone when empty, grows to the editor once a file is loaded
    // (Chloe's flow — drop one file → edit it straight away). Batch table for 2+ files lands next.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is TagEditorViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            ResizeForState(vm.HasFile);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is TagEditorViewModel vm) vm.PropertyChanged -= OnVmPropertyChanged;
        base.OnClosed(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagEditorViewModel.HasFile) && DataContext is TagEditorViewModel vm)
            ResizeForState(vm.HasFile);
    }

    private void ResizeForState(bool hasFile)
    {
        Width = hasFile ? 560 : 440;
        Height = hasFile ? 680 : 340;
        // Keep the frameless card centred as it grows/shrinks.
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is { } s)
        {
            var wa = s.WorkingArea;
            Position = new PixelPoint(
                wa.X + (int)((wa.Width - Width * RenderScaling) / 2),
                wa.Y + (int)((wa.Height - Height * RenderScaling) / 2));
        }
    }

    // Drag the window from the chrome bar (but not from interactive controls) — shared predicate.
    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Chrome gear → the separate frameless Settings card (theme + ID3 write mode). Shared singleton VM
    // so changes persist + reflect live (theme switches repaint the whole suite immediately).
    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow
        {
            DataContext = Program.Services.GetRequiredService<ViewModels.SettingsViewModel>(),
        };
        settings.ShowDialog(this);
    }

    // Brand mark (top-left) → the SHARED themed About dialog (JustPlay.UI), parameterized with JUST TAG's
    // name / tagline / version / tag glyph so it's identical to its siblings.
    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        var asm = typeof(App).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver = info?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "";
        var about = new AboutWindow(new AboutInfo(
            AppName: "JUST TAG",
            Tagline: "Single-file tag editor",
            Version: string.IsNullOrEmpty(ver) ? "" : $"Version {ver}",
            Glyph: BrandGlyphs.Tag));
        about.ShowDialog(this);
    }

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open audio file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio")
                {
                    Patterns = new[]
                    {
                        "*.mp3", "*.flac", "*.m4a", "*.aac", "*.ogg", "*.opus",
                        "*.wav", "*.aiff", "*.aif", "*.wma",
                    },
                },
                FilePickerFileTypes.All,
            },
        });

        var path = files?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && DataContext is TagEditorViewModel vm)
            vm.LoadFile(path);
    }

    private async void OnChangeCover(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TagEditorViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose cover image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" } },
                FilePickerFileTypes.All,
            },
        });

        var path = files?.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var mime = Path.GetExtension(path).ToLowerInvariant() is ".png" ? "image/png" : "image/jpeg";
            vm.SetNewCover(bytes, mime);
        }
        catch { /* unreadable image — leave the cover as-is */ }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true
            ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not TagEditorViewModel vm) return;
        var files = e.DataTransfer?.TryGetFiles()?.ToList();
        var path = files?.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        vm.LoadFile(path);
        // 1a interim: a single file edits immediately (the point). Several → the batch list lands next.
        if (files!.Count > 1) vm.Status = $"Loaded 1 of {files.Count} — the batch list lands next.";
    }
}
