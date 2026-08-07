using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Stream.ViewModels;
using JustPlay.UI;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using JustPlay.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Stream.Views;

/// <summary>
/// JUST STREAM main window - frameless floating-card shell shared with JUST PLAY via the
/// JustPlay.UI library (caption buttons = the shared <see cref="WindowControls"/>; drag
/// predicate = the shared <see cref="WindowChrome"/>; custom maximize via
/// <see cref="IFramelessWindow"/>). See CLAUDE.md "JUST suite UI philosophy".
/// </summary>
public partial class MainWindow : Window, IFramelessWindow
{
    private SettingsWindow? _settings;
    private LogWindow? _log;
    private SpectrumWindow? _spectrum;

    // Compact view (mirrors JUST PLAY): narrower window, only server-select + ON-AIR + output level.
    private const double MiniWidth = 750;   // Chloe: 450 too small, 700 still too narrow overall -> 750 (2026-07-05)
    private const double MiniHeight = 350;  // compact, explicit. Budgets the bottom KeyLegend hint bar
                                            // (added 2026-07-06, after 320 was tuned) so it can't clip the
                                            // output meters. Any slack lands as a gap ABOVE the hint bar, not a clip.
    private PixelPoint _fullPosition;    // restore position when leaving mini
    private bool _isMini;

    // Render-frame meter pump (vsync-synced, replaces the free-running timer that juddered the mini meter).
    private const double RefStep = 0.016; // fallback frame dt (first frame / hitch)
    private bool _pumpRunning;
    private TimeSpan _lastFrame;
    private LevelMeter? _meterL, _meterR; // shared output meters, fed raw each render frame

    public MainWindow()
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY - re-setting it here trips
        // Avalonia's macOS opaque-fallback (black surround); see JustPlay MainWindow ctor.

        // macOS: caption buttons sit LEFT, so the settings gear owns the top-right corner -
        // round its hover to the card radius (Button.cap.corner in JustStyles).
        if (System.OperatingSystem.IsMacOS())
            SettingsBtn.Classes.Add("corner");

        _meterL = this.FindControl<LevelMeter>("MeterL");
        _meterR = this.FindControl<LevelMeter>("MeterR");

        // In-app keyboard hotkeys (Chloe/Torres 2026-07-06: keyboard-only broadcaster - you're already
        // IN JUST STREAM to go on air, so this saves the reach for the mouse). TUNNEL so a focused
        // button/slider doesn't eat the key; the handler guards against the profile ComboBox's
        // type-ahead. C = on air / off air, R = record / stop (shown in the bottom hint bar).
        AddHandler(KeyDownEvent, OnHotkeyDown, RoutingStrategies.Tunnel);

        WindowPlacement.Track(this, "JustStream.Main");

        // The suite's custom maximize (shared): fills the work area and squares the card off. A
        // borderless transparent window's OS maximize keeps the shadow margin and the rounded corners,
        // which is not what "full screen" means.
        _maximize = FramelessMaximize.Attach(this);
        // JUST STREAM is a FIXED-SIZE window: its resize grips are hidden in the XAML and must stay
        // hidden when it comes back from maximized. Stated here rather than relied on, because the
        // shared behaviour otherwise restores them.
        _maximize.GripsVisibleWhen = () => false;
    }

    private readonly FramelessMaximize _maximize;

    private void UpdateChromeForState() => _maximize.Apply();

    // C = connect toggle, R = record toggle. Plain keys only (no modifier), and never while a text
    // field or a ComboBox has focus (so its type-ahead / editing still works).
    private void OnHotkeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is not StreamViewModel vm) return;
        if (FocusManager?.GetFocusedElement() is TextBox or ComboBox) return;

        switch (e.Key)
        {
            case Key.C:
                if (vm.ToggleConnectCommand.CanExecute(null)) vm.ToggleConnectCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.R:
                if (vm.ToggleRecordCommand.CanExecute(null)) vm.ToggleRecordCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateChromeForState();
        if (DataContext is StreamViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            // Pump meters/lamp/time from the RENDER frame (vsync-synced) instead of a free-running timer
            // - the timer beat against vsync and juddered the meter on the narrower mini bar.
            _pumpRunning = true;
            RequestAnimationFrame(OnRenderFrame);
        }
        // Stay sized-to-content (SizeToContent=Height, from XAML) in the full view so the window
        // AUTO-GROWS/shrinks to fit its content - e.g. when the App-source controls appear (taller)
        // or a Device source is chosen (shorter). STREAM's manual resize grips + custom maximize are
        // disabled (CanResize=False), so nothing needs a frozen height; freezing it was exactly what
        // clipped the taller App-mode layout. Mini switches to Manual for its explicit compact size.
    }

    // Frame-synced meter pump: re-arms each frame, so updates land exactly on render frames (no
    // timer-vs-vsync judder). dt drives the ballistics -> identical feel at 60 / 120 / 144 Hz.
    private void OnRenderFrame(TimeSpan now)
    {
        if (!_pumpRunning) return;
        var dt = _lastFrame == TimeSpan.Zero ? RefStep : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0 || dt > 0.25) dt = RefStep; // clamp the first frame and any hitch
        if (DataContext is StreamViewModel vm)
        {
            vm.PumpFrame(dt);
            _meterL?.Push(vm.OutLevelLeft, dt);
            _meterR?.Push(vm.OutLevelRight, dt);
        }
        RequestAnimationFrame(OnRenderFrame);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pumpRunning = false;
        if (DataContext is StreamViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }

    // -- Mini-player view (mirrors JUST PLAY's ApplyViewMode) -----------------
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
            // Mini is the SAME window, ONLY smaller + fewer rows shown. NO behaviour differs - no
            // topmost, no transparency change. (Chloe: "der einzige Unterschied ist kleiner + es fehlen Dinge".)
            // Switch OFF auto-fit so the explicit compact height sticks (SizeToContent would re-grow it).
            SizeToContent = SizeToContent.Manual;
            // CRITICAL: the XAML mins (MinWidth=920, MinHeight=420) would CLAMP the mini size back up -
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
            // Back to auto-fit: the window sizes its height to the full-view content again (and keeps
            // tracking it - so an App-source switch grows/shrinks the window on its own).
            SizeToContent = SizeToContent.Height;
            Position = _fullPosition;
        }
        UpdateChromeForState();
    }

    // Drag the window from the chrome bar (but not from interactive controls) - shared predicate.
    private void OnChromePressed(object? sender, PointerPressedEventArgs e) =>
        // Drag from empty chrome, DOUBLE-click to maximize/restore - one shared gesture.
        WindowChrome.HandlePress(this, e);

    // -- Custom maximize (borderless window has no OS maximize) ---------------

    /// <summary>True while the custom work-area maximize is active (for WindowPlacement).</summary>
    public bool IsMaximized => _maximize.IsMaximized;

    public void ToggleMaximize() => _maximize.Toggle();

    // -- Custom edge/corner resize (manual; the borderless window has no OS resize frame) --
    private bool _resizing, _wEdge, _eEdge, _nEdge, _sEdge;
    private PixelPoint _pointerStart, _posStart;
    private double _wStartPx, _hStartPx;

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsMaximized) return;
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

    // -- About ---------------------------------------------------------------
    // Brand mark (top-left) -> the SHARED themed About dialog (JustPlay.UI), parameterized with
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

    // -- Settings ------------------------------------------------------------
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

    // -- Spectrum analyzer -------------------------------------------------------
    // Opens the SHARED spectrum window (JustPlay.UI) over the broadcast bus. The capture engine is
    // an ISpectrumSource (DRY/WET taps + limiter GR), so it plugs in exactly like JUST PLAY's engine.
    // OUT meter suppressed - STREAM already has its own output meter on the main bar. Single-instance.
    private void OnSpectrum(object? sender, RoutedEventArgs e)
    {
        if (_spectrum is { } w)
        {
            w.Activate();
            return;
        }
        var source = Program.Services.GetService<IAudioInputEngine>();
        _spectrum = new SpectrumWindow(source, showOutputLevels: false)
        {
            Icon = Icon, // reuse the main window's rendered brand icon
        };
        _spectrum.Closed += (_, _) => _spectrum = null;
        _spectrum.Show(this);
    }

    // -- Event log -------------------------------------------------------------
    // The log lives in its own frameless window now (not the main console). Opening it clears the
    // unread marker on the chrome log button.
    private void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as StreamViewModel;
        if (_log is { } w)
        {
            w.Activate();
            vm?.EventLog.MarkRead();
            return;
        }
        _log = new LogWindow
        {
            DataContext = vm?.EventLog, // the shared log feed
            Icon = Icon, // reuse the main window's rendered brand icon
        };
        WindowPlacement.Track(_log, "JustStream.Log"); // the shared window can't hard-code the key
        _log.Closed += (_, _) => _log = null;
        _log.Show(this);
        vm?.EventLog.MarkRead();
    }

    // -- REC right-click -> "Open recordings folder" ------------------------------
    // Creates the folder if it doesn't exist yet (it's created lazily on first record start,
    // but if you're asking to SEE it, making it is clearly the intent) and opens it in the
    // OS file manager. UseShellExecute so Windows resolves the folder to an Explorer window.
    private void OnOpenRecordingsFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StreamViewModel vm) return;
        try
        {
            var folder = vm.EffectiveRecordingFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // never-crash rule: a missing drive / denied path must not take the window down.
            Console.WriteLine($"[JUST STREAM] Open recordings folder failed: {ex.Message}");
        }
    }

    // -- Preset right-click menu (Replace / Rename / Delete) ---------------------
    // Click handlers, NOT Command bindings: a ContextMenu is a popup OUTSIDE the control's visual tree,
    // so RelativeSource/PlacementTarget command bindings resolve null and the items render disabled.
    // The handler reads the clicked preset from the MenuItem DataContext (falls back to the chip via
    // PlacementTarget) and the VM from this window. Same pattern as JUST PLAY's TweaksPanel.
    private void OnPresetReplace(object? sender, RoutedEventArgs e) => RunPresetAction(sender, vm => vm.ReplacePresetCommand);
    private void OnPresetRename(object? sender, RoutedEventArgs e)  => RunPresetAction(sender, vm => vm.RenamePresetCommand);
    private void OnPresetDelete(object? sender, RoutedEventArgs e)  => RunPresetAction(sender, vm => vm.DeletePresetCommand);

    private void RunPresetAction(object? sender, Func<StreamViewModel, ICommand> pick)
    {
        if (DataContext is not StreamViewModel vm || sender is not MenuItem mi) return;
        var preset = mi.DataContext as DspPreset
                     ?? (mi.FindLogicalAncestorOfType<ContextMenu>()?.PlacementTarget as Control)?.DataContext as DspPreset;
        if (preset is null) return;
        pick(vm).Execute(preset);
    }
}
