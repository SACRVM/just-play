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
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;

namespace JustPlay.App.Views;

public partial class MainWindow : Window, IFramelessWindow
{
    // Window dimensions include the 20/22-px margin around the inner card so the drop
    // shadow has room to bloom outside the rounded corners. Visible card is 1280x820 / 640x660.
    private const double FullW = 1320, FullH = 864;
    private const double MiniW = 680, MiniH = 702;

    private PixelPoint _lastFullPosition;

    public MainWindow()
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY - never re-set it (the old
        // "belt-and-braces"). Avalonia's macOS backend is not idempotent: re-setting an
        // already-active level exits through the unsupported-level fallback and flips the
        // window to OPAQUE (black surround). TopLevelImpl.SetTransparencyLevelHint,
        // verified against release/12.0.3; the XAML string IS honoured there.

        DragDrop.AddDropHandler(this, OnDrop);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DataContextChanged += OnDataContextChanged;

        // Space is ALWAYS play/pause (unless typing or the settings overlay is open). Registered in the
        // TUNNEL phase so it fires BEFORE the focused control - otherwise a focused button (e.g. the "..."
        // list-menu button) would treat Space as a click and open its flyout. See OnSpaceKey.
        AddHandler(KeyDownEvent, OnSpaceKey, RoutingStrategies.Tunnel);

        // Don't persist bounds while in mini mode (fixed compact size, not a "restorable" layout).
        WindowPlacement.Track(this, "JustPlay.Main", () => ViewModel is not { IsMini: true });

        // The SHARED edge/corner resize - see the note further down for what this replaced. Mini mode
        // is a fixed compact size, so it is gated out; maximized needs no gate, because the card's
        // margin goes to 0 and the grip band follows it.
        FramelessResizeBehavior.Attach(this, ResizeGrips, this.FindControl<Border>("RootCard"),
                                       () => !IsMaximized && ViewModel is not { IsMini: true });

        // The suite's custom maximize (shared). Mini mode hides the resize grips for its own reason,
        // so that condition is handed in rather than baked into the behaviour.
        _maximize = FramelessMaximize.Attach(this);
        _maximize.GripsVisibleWhen = () => ViewModel is not { IsMini: true };
    }

    private readonly FramelessMaximize _maximize;

    // Global Space -> play/pause. Dialog windows (About/Transfer/Input) are separate focus roots and an
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

    // -- Graceful-quit fade --------------------------------------------------
    // When the user closes the window mid-playback (X button, Alt-F4, or the
    // self-update path that calls desktop.Shutdown()), we fade the mixer output
    // to silence (~200 ms) before letting Avalonia dispose the engine, preventing
    // the hard digital click/buzz of an abrupt BASS free.
    //
    // Pattern: cancel the FIRST close -> run the async fade -> call Close() again
    // with _closingFadeComplete = true so the second pass skips the fade and
    // lets Avalonia's normal close/dispose pipeline finish.
    //
    // Guard against re-entrance: if Close() is called a second time while the
    // fade is still awaiting (e.g. the user hammers Alt-F4), the bool is already
    // true and we fall through immediately - no infinite loop.
    private bool _closingFadeComplete;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_closingFadeComplete)
            return; // second pass from our own Close() call below - let it proceed

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

    // -- Custom maximize -----------------------------------------------------
    // A borderless transparent window (WindowDecorations=None) gets no OS resize frame
    // (WS_THICKFRAME is only set when Decorations != None - verified in Avalonia's Win32
    // WindowImpl), and any OS chrome (BorderOnly / ExtendClientArea) breaks the transparent
    // floating-card look (ugly border, or a dark DWM backdrop). So we drive resize + maximize
    // ourselves and keep the window fully custom.

    /// <summary>True while the custom work-area maximize is active (for WindowPlacement).</summary>
    public bool IsMaximized => _maximize.IsMaximized;

    /// <summary>Toggle the shared custom maximize: fills the current screen's work area (respects the
    /// taskbar) and squares the card off. See FramelessMaximize for why the OS one will not do.</summary>
    public void ToggleMaximize() => _maximize.Toggle();

    private void UpdateChromeForState() => _maximize.Apply();

    // -- Edge/corner resize ----------------------------------------------------------------------
    //
    // (!) This window used to carry its OWN copy of the pointer maths - a byte-for-byte duplicate of
    // FramelessResizeBehavior, which had been extracted FROM here so every JUST window would resize
    // identically, and which this window then never adopted. Six windows used the shared one, this
    // one used its twin, and the twin is where a stray debug Console.WriteLine had been sitting on
    // every mouse-up. Deleted in favour of the shared behaviour.
    //
    // The grip band comes from RootCard's own margin now, so it ends exactly where the card begins
    // instead of at a hand-typed 20 - see FramelessResizeBehavior. Maximizing zeroes that margin and
    // the grips go with it; MINI mode does not, so it still needs the gate below.

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
        // Hardened: dropping files - especially from a flaky/slow NETWORK SHARE (NAS, UNC paths) - must
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
