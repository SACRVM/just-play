using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JustPlay.Tag.ViewModels;
using JustPlay.UI;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using JustPlay.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Tag.Views;

/// <summary>
/// JUST TAG's window: the PRE CUE FINDER's frameless card and chrome, an mp3tag-shaped body
/// (folders - files - editor), and the SHARED <see cref="TagEditorPanel"/> as the docked sidebar.
///
/// <para>The code-behind holds only what needs a <see cref="TopLevel"/> (the folder picker, the
/// dialogs) and the clicks that move between folders. Every decision about a FILE lives in the
/// shared view model, so JUST TAG and the floating editor in JUST PLAY cannot drift apart.</para>
/// </summary>
public partial class MainWindow : Window, IFramelessWindow
{
    /// <summary>
    /// Guards the selection handler while we put the selection BACK after a cancelled switch -
    /// without it, restoring the row would re-enter the handler and ask about the same unsaved
    /// edits a second time.
    /// </summary>
    private bool _restoringSelection;

    private bool _forceClose;

    private readonly FramelessMaximize _maximize;

    /// <summary>True while the custom work-area maximize is active (read by WindowPlacement, so the
    /// screen-filling bounds are never persisted as the window's normal size).</summary>
    public bool IsMaximized => _maximize.IsMaximized;

    /// <summary>The shared custom maximize - see FramelessMaximize for why the OS one will not do.</summary>
    public void ToggleMaximize() => _maximize.Toggle();

    public MainWindow()
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY - re-setting it here trips Avalonia's
        // macOS opaque fallback (black surround); measured 2026-07-31.

        FramelessResizeBehavior.Attach(this, ResizeGrips);
        WindowPlacement.Track(this, "JustTag.Main");

        // (!) JUST TAG carried the .maximized STYLE from the day it was built, but nothing ever set the
        // class - so its maximize left the transparent shadow frame and the rounded corners in place
        // (Chloe 2026-08-05: "fullscreen heisst bildschirmfuellend"). It implements IFramelessWindow now,
        // through the SHARED behaviour, so it cannot drift from its siblings again.
        _maximize = FramelessMaximize.Attach(this);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Fetch-what-you-see: a row that scrolls into view is read AHEAD of the background pass, so a
        // 1,200-file folder fills the screen first instead of the top of the list first (the Finder's
        // mechanic, verbatim).
        FileList.ContainerPrepared += OnFileContainerPrepared;

        // OS file-copy drag-OUT, the same gesture every other JUST list has (Chloe 2026-08-06: "drag
        // and drop aus just tag geht nicht - wieso? wir haben das aktiv ueberall"). It was missing
        // because RowDragBehavior lived inside JustPlay.App and this app simply could not see it; it
        // is in JustPlay.UI now, so the three lists share ONE implementation instead of two copies.
        // Multi-select is already handled inside: dragging any row of a multi-row selection takes the
        // whole selection with it.
        RowDragBehavior.Attach(FileList, dc => (dc as FileRow)?.Path);
        // Folders drag out as folders. ".." is the way UP, not a thing you own - and a playlist row is
        // a VIRTUAL folder whose path is an .m3u file, so it drags out as that file, which is right.
        RowDragBehavior.Attach(FolderList, dc => dc is FolderRow { IsUp: false } f ? f.Path : null);

        // (!) TUNNEL + handledEventsToo. A ListBoxItem marks the plain arrow / Enter keys handled in
        // its own KeyDown, so a bubbling handler on the window would never see them - the same
        // pitfall the PRE CUE FINDER documents. KeyUp is caught for the Space-clicks-a-focused-button
        // trap; PointerReleased hands focus back to the active pane after a click on chrome.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // The settings PANEL runs on its own view model (the DI singleton), so a theme switch still
        // repaints the whole suite live. The window's DataContext is the tagger and stays that way -
        // binding the panel through it would only have coupled two unrelated things.
        Settings.DataContext = Program.Services.GetRequiredService<SettingsViewModel>();
        Settings.CloseRequested += () => Vm?.CloseSettings();
    }

    private void OnFileContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (Vm is { } vm && e.Container.DataContext is FileRow row) vm.HydrateVisible(row);
    }

    /// <summary>
    /// Launched ON a file (<c>JustTag.exe "...\track.flac"</c>, or a drop on the .exe): the view model has
    /// already opened its folder, so all that is left is to land on the file itself. Posted after Loaded
    /// because the list needs its containers before a selection means anything.
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (Vm is not { StartFile: { Length: > 0 } file } vm) return;

        Dispatcher.UIThread.Post(() =>
        {
            RestoreSelection(file);
            if (FileList.SelectedItem is FileRow row) vm.Editor.Load(row.Path);
        }, DispatcherPriority.Loaded);
    }

    private TaggerViewModel? Vm => DataContext as TaggerViewModel;

    // Editor / FileList / ResizeGrips come from Avalonia's x:Name generator - declaring them here
    // as well is a CS0102 collision, not a convenience.

    // -- Browsing --------------------------------------------------------------------------------

    /// <summary>
    /// A single click on a folder row OPENS it - ".." goes up, a name goes in. One click is one
    /// level, the same rule the Finder's folder pane follows.
    /// </summary>
    private void OnFolderTapped(object? sender, TappedEventArgs e)
    {
        // A CLICK does not move the keyboard. You are working on the left with the mouse; taking the
        // pane focus to the right would dim the pane under your pointer (Chloe 2026-08-06: "wenn ich
        // links nen ordner waehle warum fokusierst du dann die files? hab ich das je gesagt?" - no).
        if (sender is not ListBox { SelectedItem: FolderRow row }) return;
        ActivateFolder(row, moveFocus: false);
    }

    /// <summary>The NAME header - the one column the host draws itself, so its sort click lands here
    /// rather than inside the shared strip. Tag carries the column id.</summary>
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { Ready: true } vm) return;   // locked while the folder is still being listed/read
        if (sender is Control { Tag: string id }) vm.Columns.SortBy(id);
    }

    private void OnClearSearch(object? sender, RoutedEventArgs e) => Vm?.ClearSearch();

    private void OnToggleSecond(object? sender, RoutedEventArgs e) => Vm?.ToggleSecond();

    private void OnShowEditor(object? sender, RoutedEventArgs e) => Vm?.ShowTab(TagPane.Editor);

    private void OnShowAnalysis(object? sender, RoutedEventArgs e) => Vm?.ShowTab(TagPane.Analysis);

    private void OnShowFilter(object? sender, RoutedEventArgs e) => Vm?.ShowTab(TagPane.Filter);

    // -- Preview ---------------------------------------------------------------------------------

    /// <summary>Play or pause whatever the file list is pointing at.</summary>
    private void PreviewSelected()
    {
        if (Vm is not { } vm || FileList.SelectedItem is not FileRow row) return;
        vm.Preview.Toggle(row.Path);
    }

    private void OnPreviewToggle(object? sender, RoutedEventArgs e) => PreviewSelected();

    /// <summary>A double-click starts listening - the gesture that means "open this" everywhere.</summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e) => PreviewSelected();

    // -- Keyboard --------------------------------------------------------------------------------
    //
    // JUST TAG is meant to be driveable without the mouse, exactly like the PRE CUE FINDER - the
    // shell was copied from it and the BEHAVIOUR was not, which left a browser you could look at but
    // not walk through (Chloe 2026-08-06: "just tag soll bitte nicht weniger koennen als pre cue
    // finder was navigation und suche angeht ... ergibt null sinn oder?"). It does not.
    //
    // Routed on the TUNNEL phase with handledEventsToo, for the reason the Finder documents: a
    // ListBoxItem marks the plain arrow/Enter keys handled before a bubble handler would ever see
    // them, so a bubbling handler is silently dead.

    /// <summary>A text-entry control owns the keyboard - the search box, a tag field. Everything
    /// below stands down so letters, space and Tab-between-fields work like they do anywhere else.</summary>
    private bool IsTextBoxFocused()
    {
        var f = FocusManager?.GetFocusedElement();
        return f is TextBox || (f as Visual)?.FindAncestorOfType<TextBox>() is not null;
    }

    private void FocusFiles() => FileList.Focus();
    private void FocusFolders() => FolderList.Focus();

    private void RestorePaneFocus()
    {
        if (Vm?.FoldersActive == true) FocusFolders(); else FocusFiles();
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        // Escape unwinds ONE layer at a time - settings panel, then a focused text box, then the
        // window. Same ladder as the Finder's, with the settings PANEL in the overlay's place.
        if (e.Key == Key.Escape)
        {
            if (vm.IsSettingsOpen) vm.CloseSettings();
            else if (IsTextBoxFocused()) RestorePaneFocus();   // blur the field, don't close the window
            else Close();
            e.Handled = true;
            return;
        }

        // TAB swaps the two LIST panes. Inside a text field it is left alone on purpose: moving from
        // one tag field to the next is what Tab means in a form, and stealing it there would be worse
        // than not having the pane switch at all. (This window opts out of the suite-wide
        // SuppressTab - see the XAML - because it repurposes the key.)
        if (e.Key == Key.Tab && !IsTextBoxFocused())
        {
            if (vm.FoldersActive) { vm.Activate(TaggerViewModel.FocusPane.Files); FocusFiles(); }
            else { vm.Activate(TaggerViewModel.FocusPane.Folders); FocusFolders(); }
            e.Handled = true;
            return;
        }

        // A field has the keyboard: nothing below runs.
        if (IsTextBoxFocused()) return;

        var inFolders = vm.FoldersActive;
        var list = inFolders ? FolderList : FileList;

        switch (e.Key)
        {
            case Key.Up:
                ListNav.Move(list, -1);
                e.Handled = true;
                break;
            case Key.Down:
                ListNav.Move(list, +1);
                e.Handled = true;
                break;
            case Key.PageUp:
                ListNav.Move(list, -ListNav.PageStep);
                e.Handled = true;
                break;
            case Key.PageDown:
                ListNav.Move(list, +ListNav.PageStep);
                e.Handled = true;
                break;
            case Key.Home:
                ListNav.MoveTo(list, 0);
                e.Handled = true;
                break;
            case Key.End:
                ListNav.MoveTo(list, int.MaxValue);
                e.Handled = true;
                break;

            case Key.Enter:
                // Windows' meaning of Enter is "open the thing under the cursor". In the folder pane
                // that is going there; in the file pane, opening a song is playing it - so Enter is
                // play/pause on the selected row (Chloe 2026-08-06).
                if (inFolders) ActivateFolderRow();
                else PreviewSelected();
                e.Handled = true;
                break;

            case Key.Space:
                // The Finder's one MODE key: play / pause, from either pane.
                PreviewSelected();
                e.Handled = true;
                break;

            case Key.Back when inFolders:
                vm.GoUp();   // the file pane ignores Backspace, same as the Finder
                e.Handled = true;
                break;

            case Key.Left when e.KeyModifiers == KeyModifiers.None:
            case Key.Right when e.KeyModifiers == KeyModifiers.None:
                // Panes switch with Tab, not arrows - swallow any stray horizontal focus jump.
                (e.Source as IInputElement)?.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// (!) Avalonia's Button raises Click on Space KEYUP whenever it IsFocused - it checks only
    /// IsFocused, not whether the KeyDown was handled. Without this, Space and Enter would ALSO press
    /// whatever button last took focus (a caption button, a tab). The Finder documents the same trap;
    /// a text field keeps its own keys.
    /// </summary>
    private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
    {
        if (Vm is null || IsTextBoxFocused()) return;
        if (e.Key is Key.Space or Key.Enter) e.Handled = true;
    }

    /// <summary>
    /// After a click that did NOT land in one of the two list panes (a chrome button, a tab, the
    /// splitter), hand the keyboard back to the active pane - otherwise a focused button keeps focus
    /// and swallows the next Space or Enter.
    /// </summary>
    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only the LEFT button moves focus onto buttons; a right-click opens a context menu and the
        // bounce would fight the popup.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (Vm is null || IsTextBoxFocused()) return;
        if (FocusManager?.GetFocusedElement() is Visual v && v.FindAncestorOfType<ListBox>() is not null) return;
        // A click in the right-hand pane belongs to the right-hand pane - do not yank focus back.
        if (Vm.PanelActive) return;
        RestorePaneFocus();
    }

    /// <summary>
    /// Enter (or a click) on the folder row under the cursor. ".." goes up; a playlist fills the file
    /// pane and hands it the keyboard.
    ///
    /// <para>The LEAF rule: a folder with no sub-folders and no playlists of its own is entered AND
    /// the keyboard moves straight to the file list, because the folder pane would otherwise be
    /// showing nothing but ".." for you to sit on. Deciding that means enumerating the folder, which
    /// on a network share is a round trip - so it happens off the UI thread and the focus hand-off
    /// comes back on it.</para>
    /// </summary>
    /// <summary>Enter on the folder row under the cursor. Unlike a click, this DOES hand the keyboard
    /// on when the row opens a list - you are driving by keyboard, and the next thing you want to move
    /// through is the tracks.</summary>
    private void ActivateFolderRow()
    {
        if (FolderList.SelectedItem is FolderRow row) ActivateFolder(row, moveFocus: true);
    }

    /// <summary>
    /// One rule for the folder row, so a click and Enter can never disagree. ".." goes up. A playlist
    /// AND a leaf folder fill the FILE pane and hand it the keyboard - they are lists of songs, not
    /// places (Chloe 2026-08-06). Anything else descends.
    ///
    /// <para>Leaf-ness is decided when the folder is LISTED, not here, so this needs no round trip
    /// and the glyph on the row has already told you which of the two it is.</para>
    /// </summary>
    private void ActivateFolder(FolderRow row, bool moveFocus)
    {
        if (Vm is not { } vm) return;

        if (row.IsUp) { vm.GoUp(); return; }

        if (row.IsPlaylist) { OpenAsList(vm, row.Path, playlist: true, moveFocus); return; }

        // Is this a LEAF (nothing to browse into)? Decided HERE, when the row is activated - the
        // Finder's shape exactly. It is one directory check, off the UI thread because on a share it
        // is a round trip, and the answer is needed once per click rather than for every row in the
        // listing (which is what froze the app on a library root, 2026-08-06).
        var path = row.Path;
        Task.Run(() => TaggerViewModel.HasNavigableChildren(path))
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (Vm is not { } v) return;
                if (t.Result) v.Open(path); else OpenAsList(v, path, playlist: false, moveFocus);
            }), TaskScheduler.Default);
    }

    /// <summary>A playlist or a leaf folder: fill the FILE pane. The folder pane stays where it is -
    /// these are lists of songs, not places. <paramref name="moveFocus"/> is the KEYBOARD's business
    /// only: Enter hands the cursor on, a mouse click leaves it exactly where you put it.</summary>
    private void OpenAsList(TaggerViewModel vm, string path, bool playlist, bool moveFocus)
    {
        if (playlist) vm.OpenPlaylist(path); else vm.OpenFolderTracks(path);
        if (!moveFocus) return;
        vm.Activate(TaggerViewModel.FocusPane.Files);
        FocusFiles();
    }

    private void OnCrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string path }) Vm?.Open(path);
    }

    // The focused pane owns the header highlight, and GotFocus (which bubbles up from whatever inside
    // the pane actually took focus) is the single source of truth - so a click, TAB and a programmatic
    // focus all keep the cue honest. The EDITOR / ANALYSIS / FILTER side is a pane like the other two:
    // typing in a tag field lights ITS header, not the file list's (Chloe 2026-08-06).
    private void OnFoldersGotFocus(object? sender, RoutedEventArgs e) =>
        Vm?.Activate(TaggerViewModel.FocusPane.Folders);

    private void OnFilesGotFocus(object? sender, RoutedEventArgs e) =>
        Vm?.Activate(TaggerViewModel.FocusPane.Files);

    private void OnPanelGotFocus(object? sender, RoutedEventArgs e) =>
        Vm?.Activate(TaggerViewModel.FocusPane.Panel);

    // -- Drop a folder ---------------------------------------------------------------------------

    /// <summary>
    /// A folder dropped anywhere on the window opens it. Dropping FILES opens the folder they are
    /// in and selects the first one - the alternative (refusing the drop) would be pedantic about a
    /// gesture whose intent is obvious.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = PathsFrom(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;

        var dropped = PathsFrom(e);
        if (dropped.Count == 0) return;

        var first = dropped[0];
        if (Directory.Exists(first))
        {
            vm.Open(first);
            return;
        }

        var folder = Path.GetDirectoryName(first);
        if (string.IsNullOrEmpty(folder)) return;

        vm.Open(folder);
        RestoreSelection(first);          // land on the file that was actually dropped
        if (FileList.SelectedItem is FileRow row) vm.Editor.Load(row.Path);
    }

    /// <summary>The dropped local paths. Avalonia 12 exposes them via DataTransfer, not the old
    /// <c>e.Data</c>; a drop from a flaky network share must never throw out of a handler.</summary>
    private static IReadOnlyList<string> PathsFrom(DragEventArgs e)
    {
        try
        {
            return e.DataTransfer?.TryGetFiles()?
                       .Select(f => f.TryGetLocalPath())
                       .Where(p => !string.IsNullOrEmpty(p))
                       .Select(p => p!)
                       .ToList()
                   ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// A different file was picked. The unsaved-edits question is asked BEFORE the editor
    /// retargets, and a Cancel puts the selection back where it was - in a docked sidebar, a list
    /// and an editor showing two different files would be a lie about what Save is about to write.
    /// </summary>
    private async void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // The SET first, and unconditionally: how many rows are selected is a display fact, and it has
        // to stay right even when the editor refuses to leave a dirty file below.
        vm.SetSelection((sender as ListBox)?.SelectedItems?.OfType<FileRow>());

        if (_restoringSelection || Editor is not { } panel) return;

        var picked = (sender as ListBox)?.SelectedItem as FileRow;
        if (picked is not null && string.Equals(picked.Path, vm.Editor.FilePath,
                                                StringComparison.OrdinalIgnoreCase)) return;

        if (!await panel.ConfirmLeaveAsync(this))
        {
            RestoreSelection(vm.Editor.FilePath);
            return;
        }

        if (picked is null) vm.Editor.Clear();
        else vm.Editor.Load(picked.Path);
    }

    /// <summary>Point the list back at whatever the editor is actually holding.</summary>
    private void RestoreSelection(string? path)
    {
        if (FileList is not { } list || Vm is not { } vm) return;

        _restoringSelection = true;
        try
        {
            list.SelectedItem = path is null
                ? null
                : vm.Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open folder",
                AllowMultiple = false,
            });

            if (picked?.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path) Vm?.Open(path);
        }
        catch (Exception)
        {
            // Cancelled, or a provider that refused - leave the browser where it was. `async void`
            // on an event handler must not let an exception escape.
        }
    }

    // -- Chrome ----------------------------------------------------------------------------------

    private void OnChromePressed(object? sender, PointerPressedEventArgs e) =>
        // Drag from empty chrome, DOUBLE-click to maximize/restore - one shared gesture.
        WindowChrome.HandlePress(this, e);

    /// <summary>Chrome gear -> the settings PANEL slides in from the right (theme + ID3 write mode).
    /// Shared singleton view model, so a theme switch repaints the whole suite immediately.</summary>
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => Vm?.ToggleSettings();

    /// <summary>Brand mark (top-left) -> the SHARED About dialog, parameterised with JUST TAG's name
    /// and glyph so it is identical to its siblings.</summary>
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var asm = typeof(App).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var ver = info?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "";

        var about = new AboutWindow(new AboutInfo(
            AppName: "JUST TAG",
            Tagline: "Tag editor",
            Version: string.IsNullOrEmpty(ver) ? "" : $"Version {ver}",
            Glyph: BrandGlyphs.Tag));
        _ = about.ShowDialog(this);
    }

    /// <summary>
    /// Closing with unsaved edits asks the same three-way question as switching files. The close is
    /// cancelled first and re-issued after the answer, because the dialog is async and a
    /// <see cref="Window.Closing"/> handler cannot wait.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose && Vm is { Editor.IsDirty: true })
        {
            e.Cancel = true;
            _ = ConfirmThenCloseAsync();
            return;
        }
        base.OnClosing(e);
    }

    private async Task ConfirmThenCloseAsync()
    {
        if (Editor is { } panel && !await panel.ConfirmLeaveAsync(this)) return;
        _forceClose = true;
        Close();
    }
}
