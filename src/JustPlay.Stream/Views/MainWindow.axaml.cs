using System;
using System.ComponentModel;
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

    // Mini-player view (mirrors JUST PLAY): narrower window, only server-select + ON-AIR + output level.
    private const double MiniWidth = 700;   // Chloe: 450 was too small, 700 fits the mini content
    private const double MiniHeight = 320;  // compact, explicit (runtime SizeToContent left dead space below)
    private double _fullHeight;          // captured at first open (the SizeToContent auto-fit height)
    private PixelPoint _fullPosition;    // restore position when leaving mini
    private bool _isMini;

    // Render-frame meter pump (vsync-synced, replaces the free-running timer that juddered the mini meter).
    private const double RefStep = 0.016; // fallback frame dt (first frame / hitch)
    private bool _pumpRunning;
    private TimeSpan _lastFrame;

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
        if (DataContext is StreamViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            // Pump meters/lamp/time from the RENDER frame (vsync-synced) instead of a free-running timer
            // — the timer beat against vsync and juddered the meter on the narrower mini bar.
            _pumpRunning = true;
            RequestAnimationFrame(OnRenderFrame);
        }
        // The window opens sized-to-content (SizeToContent=Height) so everything fits exactly.
        // Once laid out, capture that full height + switch to manual sizing so the resize grips +
        // custom maximize work (SizeToContent would otherwise snap the height back on every resize).
        Dispatcher.UIThread.Post(() =>
        {
            _fullHeight = Height;
            SizeToContent = SizeToContent.Manual;
        }, DispatcherPriority.Loaded);
    }

    // Frame-synced meter pump: re-arms each frame, so updates land exactly on render frames (no
    // timer-vs-vsync judder). dt drives the ballistics → identical feel at 60 / 120 / 144 Hz.
    private void OnRenderFrame(TimeSpan now)
    {
        if (!_pumpRunning) return;
        var dt = _lastFrame == TimeSpan.Zero ? RefStep : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0 || dt > 0.25) dt = RefStep; // clamp the first frame and any hitch
        (DataContext as StreamViewModel)?.PumpFrame(dt);
        RequestAnimationFrame(OnRenderFrame);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pumpRunning = false;
        if (DataContext is StreamViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }

    // ── Mini-player view (mirrors JUST PLAY's ApplyViewMode) ─────────────────
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StreamViewModel.IsMini) && sender is StreamViewModel vm)
            ApplyViewMode(vm.IsMini);
    }

    private void ApplyViewMode(bool mini)
    {
        _isMini = mini;
        if (mini)
        {
            _fullPosition = Position;
            // Mini is the SAME window, ONLY smaller + fewer rows shown. NO behaviour differs — no
            // topmost, no transparency change. (Chloe: "der einzige Unterschied ist kleiner + es fehlen Dinge".)
            // CRITICAL: the XAML mins (MinWidth=920, MinHeight=420) would CLAMP the mini size back up —
            // that's why the mini window looked unchanged. Lower the mins BEFORE setting the size.
            MinWidth = MiniWidth;
            MinHeight = MiniHeight;
            Width = MiniWidth;
            Height = MiniHeight;
        }
        else
        {
            // Restore the full-window mins (must match the XAML header values) before growing back.
            MinWidth = 920;
            MinHeight = 420;
            Width = 980;
            if (_fullHeight > 0) Height = _fullHeight;
            Position = _fullPosition;
        }
        UpdateChromeForState();
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
        // Fixed-size window: resize grips stay hidden (set in XAML); nothing to toggle here.
        this.FindControl<Border>("RootCard")?.Classes.Set("maximized", _isMaxed);
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
