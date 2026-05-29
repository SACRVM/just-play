using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using JustPlay.App.ViewModels;

namespace JustPlay.App.Views;

public partial class MainWindow : Window
{
    // Window dimensions include the 20/22-px margin around the inner card so the drop
    // shadow has room to bloom outside the rounded corners. Visible card is 1280×820 / 640×660.
    private const double FullW = 1320, FullH = 864;
    private const double MiniW = 680, MiniH = 702;

    private PixelPoint _lastFullPosition;

    public MainWindow()
    {
        InitializeComponent();

        // Belt-and-braces: also set in code (the XAML TypeConverter for IReadOnlyList
        // doesn't always honour the comma-separated string on every Avalonia minor version).
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.Blur,
        };

        DragDrop.AddDropHandler(this, OnDrop);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Console.WriteLine($"[JustPlay] ActualTransparencyLevel = {ActualTransparencyLevel}");
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsMini))
            ApplyViewMode(ViewModel!.IsMini);
    }

    private void ApplyViewMode(bool mini)
    {
        if (mini)
        {
            _lastFullPosition = Position;
            CanResize = false;
            Topmost = true;
            Width = MiniW;
            Height = MiniH;
        }
        else
        {
            CanResize = true;
            Topmost = false;
            Width = FullW;
            Height = FullH;
            Position = _lastFullPosition;
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer?.Contains(DataFormat.File) == true;
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        Console.WriteLine($"[DragOver] formats=[{string.Join(",", e.DataTransfer?.Formats.Select(f => f.Identifier) ?? Array.Empty<string>())}] hasFiles={hasFiles}");
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        Console.WriteLine($"[Drop] received, formats=[{string.Join(",", e.DataTransfer?.Formats.Select(f => f.Identifier) ?? Array.Empty<string>())}]");
        if (ViewModel is not { } vm) { Console.WriteLine("[Drop] no ViewModel"); return; }

        var items = e.DataTransfer?.TryGetFiles();
        Console.WriteLine($"[Drop] items count = {items?.Length ?? -1}");
        if (items is null) return;

        var paths = items
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        Console.WriteLine($"[Drop] resolved paths = {paths.Count}");
        if (paths.Count > 0)
            await vm.AddPathsAsync(paths);
    }
}
