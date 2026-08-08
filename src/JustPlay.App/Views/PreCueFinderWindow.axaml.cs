using System;
using System.Collections.Generic;
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
using JustPlay.UI.ViewModels;
using JustPlay.UI.Views;
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
        // Explorer / an editor / an AI-agent chat window to hand over the real path - "einen KI-Agenten
        // auf ein file aufmerksam machen" (Chloe 2026-07-08). The finder lists deliberately do NOT
        // reorder: they are sorted browse views, not a hand-ordered set. A folder drags the whole
        // folder; the ".." hop isn't draggable.
        if (this.FindControl<ListBox>("FinderList") is { } fileList)
            RowDragBehavior.Attach(fileList, dc => (dc as FinderItemViewModel)?.FullPath);
        if (this.FindControl<ListBox>("FolderList") is { } folderList)
            RowDragBehavior.Attach(folderList, dc => dc is FinderEntryViewModel { IsUp: false } fe ? fe.FullPath : null);

        // TransparencyLevelHint comes from the XAML ONLY - re-setting it here trips the
        // macOS opaque-fallback bug (see MainWindow ctor).

        // macOS: the left "PRE CUE FINDER" brand is hidden (XAML) and the chrome centre is
        // shared - at the library ROOT it shows the centered brand (MacBrand), one folder
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
        // AND Avalonia's KeyboardNavigationHandler - which runs first in the tunnel and marks Tab
        // Handled (verified vs release/12.0.3). Without handledEventsToo our Tab handler would be
        // skipped once KNH handled it, so the folders/files switch never ran (Chloe 2026-07-06).
        // This window opts OUT of the suite-wide SuppressTab (it repurposes Tab) - see the XAML.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Avalonia's Button raises Click on Space KEYUP whenever it IsFocused (Button.OnKeyUp checks only
        // IsFocused, NOT our handled KeyDown - verified vs release/12.0.3), so a clicked caption/chrome
        // button would ALSO fire on our Space ("space loest die Aktion auch aus", Chloe 2026-07-06). Two
        // guards: swallow Space/Enter on KeyUp in the main view, and bounce keyboard focus off any
        // non-list control after a click so our keys never target a focused button in the first place.
        AddHandler(KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!,
                                      this.FindControl<Border>("RootCard"));

        // Theme-tinted taskbar icon, re-rendered on every palette switch (suite rule #2).
        _themeSvc = Program.Services.GetRequiredService<IThemeService>();
        Icon = ThemedWindowIcon.Render(_themeSvc.Current, BrandGlyphs.Play);
        _themeHandler = (_, theme) =>
            Dispatcher.UIThread.Post(() => Icon = ThemedWindowIcon.Render(theme, BrandGlyphs.Play));
        _themeSvc.ThemeChanged += _themeHandler;

        WindowPlacement.Track(this, "JustPlay.Finder");

        // The suite's custom maximize (shared) - fills the work area, squares the card off, hides the
        // grips. A borderless card's OS maximize would keep the shadow margin and the rounded corners.
        _maximize = FramelessMaximize.Attach(this);
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
        _open.Show(); // independent top-level (own taskbar entry, own monitor) - not owner-modal
    }

    private PreCueFinderViewModel? ViewModel => DataContext as PreCueFinderViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is { } vm)
        {
            vm.RequestFocusFiles = FocusFiles; // VM opens playlists/leaf folders into the file pane
            // Clear the file ListBox's full selection right before the VM resets Items - Avalonia's
            // SelectionModel crashes if the bound source resets while a (multi-)selection is live.
            var fileList = this.FindControl<ListBox>("FinderList");
            vm.ClearFileSelection = () => fileList?.Selection?.Clear();
            // Modal yes/no confirm over THIS window - used by "Open playlist" before it replaces the queue.
            // Uses the SHARED suite dialog (JustPlay.UI), not an app-local copy.
            vm.ConfirmAsync = (title, message, confirm) =>
                JustPlay.UI.Views.ConfirmDialog.AskAsync(this, title, message, confirm);
            // Fetch-what-you-see: hydrate a row the moment its container is realized (scrolled into view).
            // Avalonia only prepares containers for the visible range (+ a small cache), so this IS the viewport.
            if (fileList is not null) fileList.ContainerPrepared += OnFileContainerPrepared;
        }
        ViewModel?.OnWindowOpened();
        FocusFolders(); // land in the folders pane - the Norton-Commander entry point
    }

    protected override void OnClosed(EventArgs e)
    {
        _themeSvc.ThemeChanged -= _themeHandler;
        ViewModel?.OnWindowClosed();
        _open = null;
        base.OnClosed(e);
    }

    // -- Keyboard (the whole point of this window) --------------------------

#if DEBUG
    // -- F9 frame-time readout (Debug only) ----------------------------------------------------
    // Drives the compositor with a self-renewing animation-frame request and reports, once a
    // second: how many frames actually landed, and the LONGEST gap between two of them. The gap is
    // the number that matters - 60 fps with one 90 ms hitch feels broken, and an average says
    // "58 fps, fine". Printed big on the window AND to the console, so the numbers can be read
    // back here without squinting at a screenshot.
    private TextBlock? _perfReadout;
    private bool _perfOn;
    private int _frames;
    private double _worstGapMs;
    private TimeSpan _prevFrame;
    private DateTime _windowStartUtc;

    private void TogglePerfReadout()
    {
        _perfOn = !_perfOn;

        if (_perfReadout is null && Content is Panel root)
        {
            // Fully qualified on purpose: Window itself carries FontWeight / HorizontalAlignment /
            // VerticalAlignment members, so the bare enum names bind to those and don't compile.
            _perfReadout = new TextBlock
            {
                FontSize            = 20,
                FontWeight          = Avalonia.Media.FontWeight.Bold,
                Foreground           = Avalonia.Media.Brushes.White,
                Background          = new Avalonia.Media.SolidColorBrush(
                                          Avalonia.Media.Color.Parse("#D0000000")),
                Padding             = new Thickness(12, 8),
                Margin              = new Thickness(0, 64, 44, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Top,
                IsHitTestVisible    = false,
                Text                = "measuring...",
            };
            root.Children.Add(_perfReadout);
        }

        if (_perfReadout is not null) _perfReadout.IsVisible = _perfOn;

        if (!_perfOn) return;

        _frames         = 0;
        _worstGapMs     = 0;
        _prevFrame      = default;
        _windowStartUtc = DateTime.UtcNow;
        Console.WriteLine($"[Finder] perf readout ON - transparency actual = {ActualTransparencyLevel}");
        RequestAnimationFrame(OnPerfFrame);
    }

    private void OnPerfFrame(TimeSpan now)
    {
        if (!_perfOn) return;

        if (_prevFrame != default)
        {
            var gap = (now - _prevFrame).TotalMilliseconds;
            if (gap > _worstGapMs) _worstGapMs = gap;
        }
        _prevFrame = now;
        _frames++;

        var elapsed = (DateTime.UtcNow - _windowStartUtc).TotalSeconds;
        if (elapsed >= 1.0)
        {
            var fps = _frames / elapsed;
            var line = $"{fps:F0} fps - worst {_worstGapMs:F0} ms";
            if (_perfReadout is not null) _perfReadout.Text = line;
            Console.WriteLine($"[Finder] {line} - {ActualTransparencyLevel}");

            _frames         = 0;
            _worstGapMs     = 0;
            _windowStartUtc = DateTime.UtcNow;
        }

        RequestAnimationFrame(OnPerfFrame);   // keep the frame clock running
    }
#endif

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        // Escape unwinds one layer at a time: help -> settings -> filter search box -> window.
        if (e.Key == Key.Escape)
        {
            if (vm.HelpOpen) vm.HelpOpen = false;
            else if (vm.SettingsOpen) vm.SettingsOpen = false;
            else if (IsTextBoxFocused()) RestorePaneFocus(); // blur the FILTER search, don't close the window
            else Close();
            e.Handled = true;
            return;
        }

#if DEBUG
        // -- Perf probes, Debug builds only ----------------------------------------------------
        // Chloe 2026-07-31: "das scrollen im pre cue finder ist sehr traege ... man merkt auch wenn
        // man mit der maus ueber die liste faehrt und das row highlighting ausloest dass es
        // hinterherhinkt ... als waere die FPS im sack".
        //
        // (!!) RULED OUT by measurement, do not re-litigate: the lists' bottom-fade OpacityMask. It
        // was the obvious suspect (a mask forces its subtree through an offscreen layer on every
        // frame the content moves) and removing it changed NOTHING.
        //
        // That the HOVER highlight lags the pointer is the real clue: the cost is not per row and
        // not per list, it is the whole window's frame. Which points at the window itself -
        // per-pixel transparency (Transparent is first in TransparencyLevelHint) plus a big blurred
        // BoxShadow on a ClipToBounds=False card and three full-window gradient rectangles. That
        // cost scales with WINDOW SIZE, not with track count, which matches "everything is slow".
        //
        //   F9  - a readout you can actually READ. Avalonia's own FPS overlay was useless here
        //         ("die fps anzeige ist fuer die tonne - ich kann da nix erkennen"), and an average
        //         hides exactly what she feels anyway: it is the WORST frame gap that reads as lag,
        //         not the mean. So: big text, fps AND worst-frame-in-the-last-second, plus what
        //         transparency the OS actually granted (the hint is a wish list; the OS decides).
        // (!!) There WAS an F10 here that flipped the window to opaque to price per-pixel transparency.
        // It did nothing - measured, not assumed: Win32 fixes a window's transparency when it is
        // created, so assigning TransparencyLevelHint later is ignored. Testing that hypothesis needs
        // two windows created differently, not a key. Removed rather than left lying around; a probe
        // that silently measures nothing is worse than no probe.
        if (e.Key == Key.F9)
        {
            TogglePerfReadout();
            e.Handled = true;
            return;
        }

#endif

        if (e.Key == Key.F1)
        {
            vm.ToggleHelpCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // TAB switches panes - handled HERE (before the overlay guard) so it's consistent even with
        // settings/help open, and so KNH's default move is always overridden. KNH already moved focus
        // in this same tunnel pass; FocusFiles/Folders (or restoring the source) puts it right.
        if (e.Key == Key.Tab)
        {
            if (vm.SettingsOpen || vm.HelpOpen)
            {
                (e.Source as IInputElement)?.Focus(); // overlay open - Tab stays a no-op
            }
            else
            {
                // Drive the switch through the VM (ActivePane), which owns the header + selection highlight
                // AND all arrow/action routing - so Tab reliably flips panes even when the real-focus move
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
        // arrows, the x / Browse buttons) - and likewise while the FILTER search box has focus, so its
        // letters/space/+ type normally instead of triggering finder nav. Nothing below runs then.
        if (vm.SettingsOpen || vm.HelpOpen || IsTextBoxFocused()) return;

        // Two panes, Norton-Commander style: FOLDERS left, FILES right. ^/v browse the active pane;
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
                // absent on many laptops - the letter A is reachable without a modifier on every layout
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
                vm.GoUpCommand.Execute(null); // up one folder - files pane ignores Backspace
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
                // Panes switch with Tab, not arrows - swallow any stray horizontal focus jump.
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

    // The focused pane owns the header highlight - GotFocus (which bubbles up from the focused
    // ListBoxItem) is the single source of truth, so TAB, a mouse click and a programmatic focus
    // all keep the highlight honest.
    private void OnFilesGotFocus(object? sender, RoutedEventArgs e) =>
        ViewModel?.SetActivePane(PreCueFinderViewModel.FinderPane.Files);
    private void OnFoldersGotFocus(object? sender, RoutedEventArgs e) =>
        ViewModel?.SetActivePane(PreCueFinderViewModel.FinderPane.Folders);

    // -- Multi-select in the file pane -> keep the VM's SelectedItems in sync (JUST PLAY queue pattern) --
    // The cursor (SelectedItem -> Selected) still drives the cue + INFO panel; this set drives the row
    // right-click menu's bulk "Add to list" / "(Re-)analyze" actions.
    private void OnFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not ListBox lb) return;
        vm.SelectedItems.Clear();
        foreach (var it in lb.SelectedItems!.OfType<FinderItemViewModel>())
            vm.SelectedItems.Add(it);

        // The tag editor follows the SELECTION - which is the one signal it is allowed to follow (a
        // track ending must never retarget it; see TagEditorWindow). More than one row turns it into
        // a multi-file edit, the same panel and the same rules JUST TAG docks.
        if (_tagEditor is not null)
            _ = _tagEditor.SetSelectionAsync(TagTargets(vm));
    }

    /// <summary>
    /// The current file selection as editor targets. The tags ride along from the ROWS rather than
    /// being read again - and where a row has not been hydrated yet the editor reads that one itself,
    /// so a selection made while a big folder is still filling is answered correctly rather than as
    /// "these files all differ".
    /// </summary>
    private static IReadOnlyList<TagTarget> TagTargets(PreCueFinderViewModel vm)
    {
        if (vm.SelectedItems.Count > 0)
            return vm.SelectedItems.Select(i => new TagTarget(i.FullPath, i.Track.Model.Metadata))
                                   .ToList();

        return vm.Selected is { } one ? [new TagTarget(one.FullPath, one.Track.Model.Metadata)] : [];
    }

    // -- The shared, always-on-top tag editor ----------------------------------------------------
    // One window per finder. It is JustPlay.UI's TagEditorWindow, the exact control JUST TAG docks
    // as its sidebar - so a fix made here is a fix in both, by construction.

    private TagEditorWindow? _tagEditor;

    private void OnEditTags(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (_tagEditor is null)
        {
            var editor = vm.Main.CreateTagEditor();
            editor.Saved += path => vm.RefreshTagsFor(path);
            // A renamed file's row is stale in a way a tag re-read cannot fix (the path itself moved),
            // so the listing is reloaded - cheap, and it keeps the pane honest about what is on disk.
            editor.Renamed += (_, _) => vm.RefreshFolderCommand.Execute(null);
            _tagEditor = TagEditorWindow.Open(this, null, editor);
            // A closed Avalonia window cannot be shown again - drop the reference so the next
            // "Edit tags..." builds a fresh one instead of raising a dead window.
            _tagEditor.Closed += (_, _) => _tagEditor = null;
        }
        else
        {
            _tagEditor.Activate();
        }

        _ = _tagEditor.SetSelectionAsync(TagTargets(vm));
    }

    // Right-click a file row: if it isn't part of the current multi-selection, select just it (standard
    // explorer behaviour) so the bulk action targets what she pointed at, then refresh the menu headers.
    private void OnFilesContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var lb = this.FindControl<ListBox>("FinderList");
        if (lb is null) return;

        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as FinderItemViewModel;
        if (row is null) return; // click on empty space - keep the current selection

        if (!lb.SelectedItems!.Contains(row))
        {
            lb.SelectedItems.Clear();
            lb.SelectedItems.Add(row); // fires SelectionChanged -> syncs vm.SelectedItems + the cursor
        }
        vm.RefreshFileMenuState(); // fresh "(N)" headers as the menu opens
    }

    // Right-click a LEFT-pane entry (folder / playlist): point the selection (and its context menu) at
    // what she pointed at, so "Add to list" / "Open playlist" act on that entry - mirrors the file pane.
    private void OnFoldersContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var entry = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as FinderEntryViewModel;
        if (entry is null) return; // click on empty space - keep the current selection
        vm.SelectedEntry = entry;  // point the menu (CanAddSelectedEntry / SelectedEntryIsPlaylist) at it
    }

    // A file row's container was realized (scrolled into view) -> hydrate that row now (priority). The VM's
    // parallel background fill handles the rest; a one-shot claim keeps each file read to exactly once.
    private void OnFileContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (ViewModel is { } vm && e.Container.DataContext is FinderItemViewModel item)
            vm.HydrateVisible(item);
    }

    // KeyUp guard: our keys (Space/Enter) must never Click a focused button (Button.OnKeyUp fires
    // Click on Space release for any focused button). Swallow them in the main view; overlays keep
    // their own Space/Enter (Browse, format radios, x).
    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || vm.SettingsOpen || vm.HelpOpen || IsTextBoxFocused()) return;
        if (e.Key is Key.Space or Key.Enter) e.Handled = true;
    }

    /// <summary>A text-entry control (the FILTER search box, the settings library-root box) owns the
    /// keyboard - the finder's global key routing + KeyUp swallow must stand down so it can type.</summary>
    private bool IsTextBoxFocused()
    {
        var f = FocusManager?.GetFocusedElement();
        return f is TextBox || (f as Visual)?.FindAncestorOfType<TextBox>() is not null;
    }

    // After any click that didn't land inside one of the two list panes (a caption / chrome / breadcrumb
    // button, the splitter...), hand keyboard focus back to the active pane - so a focused button can't keep
    // focus and hijack Space/Enter (Chloe 2026-07-06: "space loest die Aktion auch aus").
    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only the LEFT button steals focus onto buttons; a right-click opens the row context menu and must
        // not trigger the focus-bounce (which could fight the popup). Chloe 2026-07-07.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (ViewModel is not { } vm || vm.SettingsOpen || vm.HelpOpen) return;
        if (IsTextBoxFocused()) return; // clicked into the FILTER search - keep focus there, don't bounce it away
        if (FocusManager?.GetFocusedElement() is Visual v && v.FindAncestorOfType<ListBox>() is not null) return;
        RestorePaneFocus();
    }

    // -- Mouse niceties ------------------------------------------------------

    /// <summary>Double-click a file = play the selected song (same as Enter).</summary>
    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.PlaySelected();

    /// <summary>Single-click a folder / playlist = activate: descend a container, or open a leaf folder /
    /// playlist's tracks (the VM hands the file pane focus). A double-click raises DoubleTapped, not a
    /// second Tapped, so it activates exactly once.</summary>
    private void OnFolderTapped(object? sender, TappedEventArgs e) => ViewModel?.ActivateEntry();

    /// <summary>Click the NAME header = sort by title (asc -> desc -> default). The data columns sort themselves
    /// inside the shared TrackDataHeader; this only serves the host-side NAME cell. Tag carries the column id.</summary>
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.Tag is string col) ViewModel?.Columns.SortBy(col);
    }

    // -- Player-bar transport buttons (mirror the keyboard shortcuts as real UI buttons - Chloe 2026-07-07).
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

    // Brand mark -> the shared About dialog, exactly like the main window (MaxView.OnAboutClick).
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
            // Start the picker at the CURRENT root - or its nearest existing ancestor if that path is
            // dead/offline (NAS) - instead of dumping the user somewhere random (Chloe 2026-07-05:
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

    // -- Frameless chrome: drag + custom maximize (MainWindow pattern) -------

    private void OnChromePressed(object? sender, PointerPressedEventArgs e) =>
        // Drag from empty chrome, DOUBLE-click to maximize/restore - one shared gesture.
        WindowChrome.HandlePress(this, e);


    private readonly FramelessMaximize _maximize;

    /// <summary>True while the custom work-area maximize is active (for WindowPlacement).</summary>
    public bool IsMaximized => _maximize.IsMaximized;

    /// <summary>The shared custom maximize - a borderless card gets no usable OS one.</summary>
    public void ToggleMaximize() => _maximize.Toggle();
}
