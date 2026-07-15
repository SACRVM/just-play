using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JustPlay.App.Controls;
using JustPlay.App.ViewModels;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;
using JustPlay.UI;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App.Views;

/// <summary>
/// PRE CUE FINDER window shell (N26 P1). All behaviour lives in
/// <see cref="PreCueFinderViewModel"/> (a singleton, so browse position and playlist state
/// survive close/reopen); this class owns only window mechanics: the global key handling,
/// frameless chrome (drag / custom maximize / shared resize grips), the themed taskbar icon,
/// and the folder picker (needs the window's StorageProvider).
/// </summary>
public partial class PreCueFinderWindow : Window, IFramelessWindow
{
    private static PreCueFinderWindow? _open;

    /// <summary>Rows moved per PageUp / PageDown.</summary>
    private const int PageStep = 12;

    private readonly EventHandler<Theme> _themeHandler;
    private readonly IThemeService _themeSvc;

    public PreCueFinderWindow()
    {
        InitializeComponent();

        // OS file/folder-copy drag-out (drag-out-only mode of RowDragBehavior; the queue additionally
        // reorders): press-drag a FILE row or a FOLDER row past a small threshold and drop it on
        // Explorer / an editor / an AI-agent chat window to hand over the real path — "einen KI-Agenten
        // auf ein file aufmerksam machen" (Chloe 2026-07-08). The finder lists deliberately do NOT
        // reorder: they are sorted browse views, not a hand-ordered set. A folder drags the whole
        // folder; the ".." hop isn't draggable.
        if (this.FindControl<ListBox>("FinderList") is { } fileList)
            RowDragBehavior.Attach(fileList, dc => (dc as FinderItemViewModel)?.FullPath);
        if (this.FindControl<ListBox>("FolderList") is { } folderList)
            RowDragBehavior.Attach(folderList, dc => dc is FinderEntryViewModel { IsUp: false } fe ? fe.FullPath : null);

        // TransparencyLevelHint comes from the XAML ONLY — re-setting it here trips the
        // macOS opaque-fallback bug (see MainWindow ctor).

        // macOS: the left "PRE CUE FINDER" brand is hidden (XAML) and the chrome centre is
        // shared — at the library ROOT it shows the centered brand (MacBrand), one folder
        // deeper the folder breadcrumb (both worlds, Chloe 2026-07-15). The settings gear
        // owns the top-right corner, so its hover gets the card radius (Button.cap.corner).
        if (OperatingSystem.IsMacOS())
        {
            Breadcrumb.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            FinderSettingsBtn.Classes.Add("corner");

            void SyncChromeCenter()
            {
                var atRoot = ViewModel is not { } vm || vm.BreadcrumbSegments.Count <= 1;
                MacBrand.IsVisible = atRoot;
                Breadcrumb.IsVisible = !atRoot;
            }
            DataContextChanged += (_, _) =>
            {
                if (ViewModel is { } vm)
                    vm.BreadcrumbSegments.CollectionChanged += (_, _) => SyncChromeCenter();
                SyncChromeCenter();
            };
            SyncChromeCenter();
        }

        // TUNNEL + handledEventsToo so the finder's keys beat BOTH the focused ListBoxItem/Button
        // AND Avalonia's KeyboardNavigationHandler — which runs first in the tunnel and marks Tab
        // Handled (verified vs release/12.0.3). Without handledEventsToo our Tab handler would be
        // skipped once KNH handled it, so the folders/files switch never ran (Chloe 2026-07-06).
        // This window opts OUT of the suite-wide SuppressTab (it repurposes Tab) — see the XAML.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Avalonia's Button raises Click on Space KEYUP whenever it IsFocused (Button.OnKeyUp checks only
        // IsFocused, NOT our handled KeyDown — verified vs release/12.0.3), so a clicked caption/chrome
        // button would ALSO fire on our Space ("space löst die Aktion auch aus", Chloe 2026-07-06). Two
        // guards: swallow Space/Enter on KeyUp in the main view, and bounce keyboard focus off any
        // non-list control after a click so our keys never target a focused button in the first place.
        AddHandler(KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!);

        // Theme-tinted taskbar icon, re-rendered on every palette switch (suite rule #2).
        _themeSvc = Program.Services.GetRequiredService<IThemeService>();
        Icon = ThemedWindowIcon.Render(_themeSvc.Current, BrandGlyphs.Play);
        _themeHandler = (_, theme) =>
            Dispatcher.UIThread.Post(() => Icon = ThemedWindowIcon.Render(theme, BrandGlyphs.Play));
        _themeSvc.ThemeChanged += _themeHandler;

        WindowPlacement.Track(this, "JustPlay.Finder");
    }

    /// <summary>Open the finder (or surface the already-open one). The viewmodel is the
    /// DI singleton, so reopening lands exactly where she left off.</summary>
    public static void ShowOrActivate()
    {
        if (_open is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }
        _open = new PreCueFinderWindow
        {
            DataContext = Program.Services.GetRequiredService<PreCueFinderViewModel>(),
        };
        _open.Show(); // independent top-level (own taskbar entry, own monitor) — not owner-modal
    }

    private PreCueFinderViewModel? ViewModel => DataContext as PreCueFinderViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is { } vm)
        {
            vm.RequestFocusFiles = FocusFiles; // VM opens playlists/leaf folders into the file pane
            // Clear the file ListBox's full selection right before the VM resets Items — Avalonia's
            // SelectionModel crashes if the bound source resets while a (multi-)selection is live.
            var fileList = this.FindControl<ListBox>("FinderList");
            vm.ClearFileSelection = () => fileList?.Selection?.Clear();
        }
        ViewModel?.OnWindowOpened();
        FocusFolders(); // land in the folders pane — the Norton-Commander entry point
    }

    protected override void OnClosed(EventArgs e)
    {
        _themeSvc.ThemeChanged -= _themeHandler;
        ViewModel?.OnWindowClosed();
        _open = null;
        base.OnClosed(e);
    }

    // ── Keyboard (the whole point of this window) ──────────────────────────

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        // Escape unwinds one layer at a time: help → settings → filter search box → window.
        if (e.Key == Key.Escape)
        {
            if (vm.HelpOpen) vm.HelpOpen = false;
            else if (vm.SettingsOpen) vm.SettingsOpen = false;
            else if (IsTextBoxFocused()) RestorePaneFocus(); // blur the FILTER search, don't close the window
            else Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1)
        {
            vm.ToggleHelpCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // TAB switches panes — handled HERE (before the overlay guard) so it's consistent even with
        // settings/help open, and so KNH's default move is always overridden. KNH already moved focus
        // in this same tunnel pass; FocusFiles/Folders (or restoring the source) puts it right.
        if (e.Key == Key.Tab)
        {
            if (vm.SettingsOpen || vm.HelpOpen)
            {
                (e.Source as IInputElement)?.Focus(); // overlay open — Tab stays a no-op
            }
            else
            {
                // Drive the switch through the VM (ActivePane), which owns the header + selection highlight
                // AND all arrow/action routing — so Tab reliably flips panes even when the real-focus move
                // is contested by KeyboardNavigationHandler. The Focus call is a best-effort nicety on top.
                var target = vm.ActivePane == PreCueFinderViewModel.FinderPane.Folders
                    ? PreCueFinderViewModel.FinderPane.Files
                    : PreCueFinderViewModel.FinderPane.Folders;
                vm.SetActivePane(target);
                if (target == PreCueFinderViewModel.FinderPane.Files) FocusFiles(); else FocusFolders();
            }
            e.Handled = true;
            return;
        }

        // While an overlay is open the keyboard belongs to it (typing the library root, ComboBox
        // arrows, the ✕ / Browse buttons) — and likewise while the FILTER search box has focus, so its
        // letters/space/+ type normally instead of triggering finder nav. Nothing below runs then.
        if (vm.SettingsOpen || vm.HelpOpen || IsTextBoxFocused()) return;

        // Two panes, Norton-Commander style: FOLDERS left, FILES right. ↑/↓ browse the active pane;
        // Enter descends / adds; Backspace goes up but ONLY in the folders pane (Chloe 2026-07-05).
        var inFolders = vm.ActivePane == PreCueFinderViewModel.FinderPane.Folders;

        switch (e.Key)
        {
            case Key.Space:
                vm.TogglePlayPause(); // one MODE: play (browse auditions) / pause (silent)
                e.Handled = true;
                break;

            case Key.Up:
                if (inFolders) vm.MoveFolderSelection(-1); else vm.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                if (inFolders) vm.MoveFolderSelection(+1); else vm.MoveSelection(+1);
                e.Handled = true;
                break;

            case Key.Enter:
                // ".." up / descend / open a leaf folder or playlist (the VM hands focus to the file
                // pane itself); in the file pane Enter PLAYS the selected song (Chloe 2026-07-06).
                if (inFolders) vm.ActivateEntry();
                else vm.PlaySelected();
                e.Handled = true;
                break;

            case Key.Add:      // numpad +
            case Key.OemPlus:  // main-row + (Shift + =)
            case Key.A when e.KeyModifiers == KeyModifiers.None:
                // "+" adds the selected song(s) to the current list. A is a layout-safe alias: on French
                // AZERTY / German QWERTZ the physical "+"/OemPlus key doesn't always map, and numpad + is
                // absent on many laptops — the letter A is reachable without a modifier on every layout
                // (Avalonia binds the letter, not the physical position). Chloe 2026-07-07.
                if (!inFolders) vm.ActivateSelected();
                e.Handled = true;
                break;

            case Key.Home:
                if (inFolders) vm.MoveFolderSelectionTo(0); else vm.MoveSelectionTo(0);
                e.Handled = true;
                break;
            case Key.End:
                if (inFolders) vm.MoveFolderSelectionTo(int.MaxValue); else vm.MoveSelectionTo(int.MaxValue);
                e.Handled = true;
                break;
            case Key.PageUp:
                if (inFolders) vm.MoveFolderSelection(-PageStep); else vm.MoveSelection(-PageStep);
                e.Handled = true;
                break;
            case Key.PageDown:
                if (inFolders) vm.MoveFolderSelection(+PageStep); else vm.MoveSelection(+PageStep);
                e.Handled = true;
                break;

            case Key.Back when inFolders:
                vm.GoUpCommand.Execute(null); // up one folder — files pane ignores Backspace
                e.Handled = true;
                break;

            case Key.Left when e.KeyModifiers == KeyModifiers.Shift:
                vm.SeekBack();
                e.Handled = true;
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.Shift:
                vm.SeekForward();
                e.Handled = true;
                break;

            case Key.Left when e.KeyModifiers == KeyModifiers.None:
            case Key.Right when e.KeyModifiers == KeyModifiers.None:
                // Panes switch with Tab, not arrows — swallow any stray horizontal focus jump.
                (e.Source as IInputElement)?.Focus();
                e.Handled = true;
                break;

            case Key.L when e.KeyModifiers == KeyModifiers.None:
                vm.ToggleLikeSelected();
                e.Handled = true;
                break;
        }
    }

    private void FocusFiles() => this.FindControl<ListBox>("FinderList")?.Focus();
    private void FocusFolders() => this.FindControl<ListBox>("FolderList")?.Focus();

    private void RestorePaneFocus()
    {
        if (ViewModel?.ActivePane == PreCueFinderViewModel.FinderPane.Files) FocusFiles();
        else FocusFolders();
    }

    // The focused pane owns the header highlight — GotFocus (which bubbles up from the focused
    // ListBoxItem) is the single source of truth, so TAB, a mouse click and a programmatic focus
    // all keep the highlight honest.
    private void OnFilesGotFocus(object? sender, RoutedEventArgs e) =>
        ViewModel?.SetActivePane(PreCueFinderViewModel.FinderPane.Files);
    private void OnFoldersGotFocus(object? sender, RoutedEventArgs e) =>
        ViewModel?.SetActivePane(PreCueFinderViewModel.FinderPane.Folders);

    // ── Multi-select in the file pane → keep the VM's SelectedItems in sync (JUST PLAY queue pattern) ──
    // The cursor (SelectedItem → Selected) still drives the cue + INFO panel; this set drives the row
    // right-click menu's bulk "Add to list" / "(Re-)analyze" actions.
    private void OnFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not ListBox lb) return;
        vm.SelectedItems.Clear();
        foreach (var it in lb.SelectedItems!.OfType<FinderItemViewModel>())
            vm.SelectedItems.Add(it);
    }

    // Right-click a file row: if it isn't part of the current multi-selection, select just it (standard
    // explorer behaviour) so the bulk action targets what she pointed at, then refresh the menu headers.
    private void OnFilesContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var lb = this.FindControl<ListBox>("FinderList");
        if (lb is null) return;

        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as FinderItemViewModel;
        if (row is null) return; // click on empty space — keep the current selection

        if (!lb.SelectedItems!.Contains(row))
        {
            lb.SelectedItems.Clear();
            lb.SelectedItems.Add(row); // fires SelectionChanged → syncs vm.SelectedItems + the cursor
        }
        vm.RefreshFileMenuState(); // fresh "(N)" headers as the menu opens
    }

    // KeyUp guard: our keys (Space/Enter) must never Click a focused button (Button.OnKeyUp fires
    // Click on Space release for any focused button). Swallow them in the main view; overlays keep
    // their own Space/Enter (Browse, format radios, ✕).
    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SettingsOpen || vm.HelpOpen || IsTextBoxFocused()) return;
        if (e.Key is Key.Space or Key.Enter) e.Handled = true;
    }

    /// <summary>A text-entry control (the FILTER search box, the settings library-root box) owns the
    /// keyboard — the finder's global key routing + KeyUp swallow must stand down so it can type.</summary>
    private bool IsTextBoxFocused()
    {
        var f = FocusManager?.GetFocusedElement();
        return f is TextBox || (f as Visual)?.FindAncestorOfType<TextBox>() is not null;
    }

    // After any click that didn't land inside one of the two list panes (a caption / chrome / breadcrumb
    // button, the splitter…), hand keyboard focus back to the active pane — so a focused button can't keep
    // focus and hijack Space/Enter (Chloe 2026-07-06: "space löst die Aktion auch aus").
    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only the LEFT button steals focus onto buttons; a right-click opens the row context menu and must
        // not trigger the focus-bounce (which could fight the popup). Chloe 2026-07-07.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (ViewModel is not { } vm || vm.SettingsOpen || vm.HelpOpen) return;
        if (IsTextBoxFocused()) return; // clicked into the FILTER search — keep focus there, don't bounce it away
        if (FocusManager?.GetFocusedElement() is Visual v && v.FindAncestorOfType<ListBox>() is not null) return;
        RestorePaneFocus();
    }

    // ── Mouse niceties ──────────────────────────────────────────────────────

    /// <summary>Double-click a file = play the selected song (same as Enter).</summary>
    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.PlaySelected();

    /// <summary>Single-click a folder / playlist = activate: descend a container, or open a leaf folder /
    /// playlist's tracks (the VM hands the file pane focus). A double-click raises DoubleTapped, not a
    /// second Tapped, so it activates exactly once.</summary>
    private void OnFolderTapped(object? sender, TappedEventArgs e) => ViewModel?.ActivateEntry();

    /// <summary>Click the NAME header = sort by title (asc → desc → default). The data columns sort themselves
    /// inside the shared TrackDataHeader; this only serves the host-side NAME cell. Tag carries the column id.</summary>
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.Tag is string col) ViewModel?.Columns.SortBy(col);
    }

    // ── Player-bar transport buttons (mirror the keyboard shortcuts as real UI buttons — Chloe 2026-07-07).
    // After the click, OnWindowPointerReleased bounces focus back to the active pane, so Space/arrows keep
    // working (the button never keeps focus and hijacks the keys).
    private void OnCuePlayPause(object? sender, RoutedEventArgs e) => ViewModel?.TogglePlayPause();
    private void OnCueSeekBack(object? sender, RoutedEventArgs e) => ViewModel?.SeekBack();
    private void OnCueSeekForward(object? sender, RoutedEventArgs e) => ViewModel?.SeekForward();
    private void OnCueAdd(object? sender, RoutedEventArgs e) => ViewModel?.ActivateSelected();

    private void OnSettingsBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is { } vm) vm.SettingsOpen = false;
        RestorePaneFocus();
    }

    private void OnHelpBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is { } vm) vm.HelpOpen = false;
        RestorePaneFocus();
    }

    // Brand mark → the shared About dialog, exactly like the main window (MaxView.OnAboutClick).
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var about = new JustPlay.UI.Views.AboutWindow(new JustPlay.UI.Views.AboutInfo(
            AppName: "JustPlay",
            Tagline: "Key-aware DJ music player",
            Version: $"Version {AppInfo.Version}",
            Glyph: BrandGlyphs.Play));
        about.ShowDialog(this);
    }

    private async void OnBrowseRoot(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Start the picker at the CURRENT root — or its nearest existing ancestor if that path is
            // dead/offline (NAS) — instead of dumping the user somewhere random (Chloe 2026-07-05:
            // "der klassiker beim folder picker"; same fix as the main window / JUST STREAM).
            var start = ViewModel?.LibraryRoot;
            while (!string.IsNullOrEmpty(start) && !Directory.Exists(start))
                start = Path.GetDirectoryName(start);

            var options = new FolderPickerOpenOptions
            {
                Title = "Choose your music library root",
                AllowMultiple = false,
            };
            if (!string.IsNullOrEmpty(start))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);

            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
            if (ViewModel is { } vm) vm.LibraryRoot = path;
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex, "Picking the finder library root");
        }
    }

    // ── Frameless chrome: drag + custom maximize (MainWindow pattern) ───────

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private bool _isMaxed;
    private PixelRect _restoreBounds;

    /// <summary>True while the custom work-area maximize is active (for WindowPlacement).</summary>
    public bool IsMaximized => _isMaxed;

    /// <summary>Custom maximize into the screen's work area, squaring the card off via the
    /// "maximized" class — a borderless window gets no OS maximize (see MainWindow).</summary>
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

        this.FindControl<Border>("RootCard")?.Classes.Set("maximized", _isMaxed);
        if (this.FindControl<Grid>("ResizeGrips") is { } grips)
            grips.IsVisible = !_isMaxed;
    }
}
