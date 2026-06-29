using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JustPlay.Stream.ViewModels;
using JustPlay.UI;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using JustPlay.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Stream.Views;

/// <summary>
/// JUST STREAM main window — frameless floating-card shell shared with JUST PLAY via the
/// JustPlay.UI library (caption buttons = the shared <see cref="WindowControls"/>; drag
/// predicate = the shared <see cref="WindowChrome"/>; custom maximize via
/// <see cref="IFramelessWindow"/>). See CLAUDE.md "JUST suite UI philosophy".
/// </summary>
public partial class MainWindow : Window, IFramelessWindow
{
    private SettingsWindow? _settings;
    private LogWindow? _log;

    public MainWindow()
    {
        InitializeComponent();

        // Belt-and-braces (the XAML IReadOnlyList TypeConverter isn't honoured on every minor version).
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.Blur,
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateChromeForState();
        // The window opens sized-to-content (SizeToContent=Height) so everything fits exactly.
        // Once laid out, switch to manual sizing so the resize grips + custom maximize work
        // (SizeToContent would otherwise snap the height back to content on every resize).
        Dispatcher.UIThread.Post(() => SizeToContent = SizeToContent.Manual, DispatcherPriority.Loaded);
    }

    // Drag the window from the chrome bar (but not from interactive controls) — shared predicate.
    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    // ── Custom maximize (borderless window has no OS maximize) ───────────────
    private bool _isMaxed;
    private PixelRect _restoreBounds;

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
            grips.IsVisible = !_isMaxed;
    }

    // ── Custom edge/corner resize (manual; the borderless window has no OS resize frame) ──
    private bool _resizing, _wEdge, _eEdge, _nEdge, _sEdge;
    private PixelPoint _pointerStart, _posStart;
    private double _wStartPx, _hStartPx;

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isMaxed) return;
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
    }

    // ── About ───────────────────────────────────────────────────────────────
    // Brand mark (top-left) → the SHARED themed About dialog (JustPlay.UI), parameterized with
    // JUST STREAM's name / tagline / version / Funkturm glyph so it's identical to JUST PLAY's.
    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        var asm = typeof(App).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver = info?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "";
        var about = new AboutWindow(new AboutInfo(
            AppName: "JUST STREAM",
            Tagline: "Broadcast streaming console",
            Version: string.IsNullOrEmpty(ver) ? "" : $"Version {ver}",
            Glyph: BrandGlyphs.RadioTower));
        about.ShowDialog(this);
    }

    // ── Settings ────────────────────────────────────────────────────────────
    private void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_settings is { } w)
        {
            w.Activate();
            return;
        }
        _settings = new SettingsWindow
        {
            DataContext = Program.Services.GetRequiredService<SettingsViewModel>(),
            Icon = Icon, // reuse the main window's rendered brand icon
        };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show(this);
    }

    // ── Event log ─────────────────────────────────────────────────────────────
    // The log lives in its own frameless window now (not the main console). Opening it clears the
    // unread marker on the chrome log button.
    private void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as StreamViewModel;
        if (_log is { } w)
        {
            w.Activate();
            vm?.MarkLogsRead();
            return;
        }
        _log = new LogWindow
        {
            DataContext = vm,
            Icon = Icon, // reuse the main window's rendered brand icon
        };
        _log.Closed += (_, _) => _log = null;
        _log.Show(this);
        vm?.MarkLogsRead();
    }
}
