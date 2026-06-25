using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JustPlay.App.ViewModels;
using JustPlay.UI;

namespace JustPlay.App.Views;

public partial class MainWindow : Window, IFramelessWindow
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

        // Space is ALWAYS play/pause (unless typing or the settings overlay is open). Registered in the
        // TUNNEL phase so it fires BEFORE the focused control — otherwise a focused button (e.g. the "…"
        // list-menu button) would treat Space as a click and open its flyout. See OnSpaceKey.
        AddHandler(KeyDownEvent, OnSpaceKey, RoutingStrategies.Tunnel);
    }

    // Global Space → play/pause. Dialog windows (About/Transfer/Input) are separate focus roots and an
    // open flyout/menu is its own popup root, so this handler never steals their keys; the TextBox guard
    // leaves typing alone, and IsTweaksOpen yields to the settings overlay (combos / edit fields there).
    private void OnSpaceKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return; // let Space type a space
        if (vm.IsTweaksOpen) return;                              // settings overlay owns its keys
        vm.TogglePlayPauseCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Console.WriteLine($"[JustPlay] ActualTransparencyLevel = {ActualTransparencyLevel}");
        UpdateChromeForState();
    }

    // ── Graceful-quit fade ──────────────────────────────────────────────────
    // When the user closes the window mid-playback (X button, Alt-F4, or the
    // self-update path that calls desktop.Shutdown()), we fade the mixer output
    // to silence (~200 ms) before letting Avalonia dispose the engine, preventing
    // the hard digital click/buzz of an abrupt BASS free.
    //
    // Pattern: cancel the FIRST close → run the async fade → call Close() again
    // with _closingFadeComplete = true so the second pass skips the fade and
    // lets Avalonia's normal close/dispose pipeline finish.
    //
    // Guard against re-entrance: if Close() is called a second time while the
    // fade is still awaiting (e.g. the user hammers Alt-F4), the bool is already
    // true and we fall through immediately — no infinite loop.
    private bool _closingFadeComplete;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_closingFadeComplete)
            return; // second pass from our own Close() call below — let it proceed

        if (DataContext is not MainWindowViewModel vm)
            return; // no VM yet (shouldn't happen in practice, but be safe)

        // Cancel this close event and kick off the async fade on the UI thread.
        e.Cancel = true;
        _ = RunFadeAndCloseAsync(vm);
    }

    private async Task RunFadeAndCloseAsync(MainWindowViewModel vm)
    {
        try
        {
            await vm.FadeBeforeQuitAsync();
        }
        catch (Exception ex)
        {
            // Don't let a fade failure prevent the app from quitting.
            Console.WriteLine($"[JustPlay] FadeBeforeQuit error (ignored): {ex.Message}");
        }
        finally
        {
            _closingFadeComplete = true;
            Close(); // re-issue the close; _closingFadeComplete = true skips OnClosing re-entry
        }
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
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // Hardened: dropping files — especially from a flaky/slow NETWORK SHARE (NAS, UNC paths) — must
        // never crash the app. Any failure is reported via the Oops dialog and swallowed here.
        try
        {
            if (ViewModel is not { } vm) return;

            var items = e.DataTransfer?.TryGetFiles();
            if (items is null) return;

            var paths = items
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList();

            // Route like the file-association / double-click path: a dropped .m3u8/.m3u playlist
            // REPLACES the queue (it's a complete set), plain audio files are ADDED without
            // hijacking playback (addOnly). OpenIncomingAsync handles the playlist-vs-audio split.
            if (paths.Count > 0)
                await vm.OpenIncomingAsync(paths, addOnly: true);
        }
        catch (System.Exception ex)
        {
            JustPlay.App.ErrorReporter.Report(ex, "Drag-and-drop add (incl. network / NAS paths)");
        }
    }
}
