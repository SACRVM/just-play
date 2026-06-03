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
        UpdateChromeForState();
    }

    // ── Custom maximize ─────────────────────────────────────────────────────
    // A borderless transparent window (WindowDecorations=None) gets no OS resize frame
    // (WS_THICKFRAME is only set when Decorations != None — verified in Avalonia's Win32
    // WindowImpl), and any OS chrome (BorderOnly / ExtendClientArea) breaks the transparent
    // floating-card look (ugly border, or a dark DWM backdrop). So we drive resize + maximize
    // ourselves and keep the window fully custom.
    private bool _isMaxed;
    private PixelRect _restoreBounds;

    /// <summary>Toggle a custom maximize that fills the current screen's work area (respects the
    /// taskbar) and squares off the card (margin/corners/shadow via the "maximized" class),
    /// restoring the exact prior bounds on toggle-off.</summary>
    public void ToggleMaximize()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        if (!_isMaxed)
        {
            _restoreBounds = new PixelRect(Position, PixelSize.FromSize(new Size(Width, Height), RenderScaling));
            var wa = screen.WorkingArea;
            Position = wa.Position;
            Width = wa.Width / RenderScaling;
            Height = wa.Height / RenderScaling;
            _isMaxed = true;
        }
        else
        {
            Position = _restoreBounds.Position;
            Width = _restoreBounds.Width / RenderScaling;
            Height = _restoreBounds.Height / RenderScaling;
            _isMaxed = false;
        }
        UpdateChromeForState();
    }

    private void UpdateChromeForState()
    {
        this.FindControl<Border>("RootCard")?.Classes.Set("maximized", _isMaxed);
        if (this.FindControl<Grid>("ResizeGrips") is { } grips)
            grips.IsVisible = !_isMaxed && !(ViewModel?.IsMini ?? false);
    }

    // ── Custom edge/corner resize (manual; the borderless window has no OS resize frame) ──
    private bool _resizing, _wEdge, _eEdge, _nEdge, _sEdge;
    private PixelPoint _pointerStart, _posStart;   // screen px, window-pos px
    private double _wStartPx, _hStartPx;           // window size px

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isMaxed || (ViewModel?.IsMini ?? false)) return;
        if (sender is not Border { Tag: string name }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _wEdge = name.Contains("West"); _eEdge = name.Contains("East");
        _nEdge = name.Contains("North"); _sEdge = name.Contains("South");
        _posStart = Position;
        _wStartPx = Width * RenderScaling;
        _hStartPx = Height * RenderScaling;
        _pointerStart = this.PointToScreen(e.GetPosition(this));
        _resizing = true;
        e.Pointer.Capture((Border)sender);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        var p = this.PointToScreen(e.GetPosition(this));
        double dx = p.X - _pointerStart.X, dy = p.Y - _pointerStart.Y;
        double scale = RenderScaling, minW = MinWidth * scale, minH = MinHeight * scale;

        double newW = _wStartPx, newH = _hStartPx;
        int newX = _posStart.X, newY = _posStart.Y;

        if (_eEdge) newW = Math.Max(minW, _wStartPx + dx);
        if (_sEdge) newH = Math.Max(minH, _hStartPx + dy);
        if (_wEdge) { newW = Math.Max(minW, _wStartPx - dx); newX = _posStart.X + (int)(_wStartPx - newW); }
        if (_nEdge) { newH = Math.Max(minH, _hStartPx - dy); newY = _posStart.Y + (int)(_hStartPx - newH); }

        Position = new PixelPoint(newX, newY);
        Width = newW / scale;
        Height = newH / scale;
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        Console.WriteLine($"[Resize] released → {Width:0}x{Height:0}");
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
        UpdateChromeForState(); // grips only in full, non-maximized mode
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
