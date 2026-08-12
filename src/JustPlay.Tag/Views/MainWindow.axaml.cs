using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
// Avalonia 12 moved the clipboard to DataTransfer; SetTextAsync survives as an EXTENSION here
// (verified in src/Avalonia.Base/Input/Platform/ClipboardExtensions.cs, release/12.0.3), so the
// using is what makes it exist rather than a package reference.
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Organise;
using JustPlay.Library;
using JustPlay.Tag.ViewModels;
using JustPlay.UI;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using JustPlay.UI.ViewModels;
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

        FramelessResizeBehavior.Attach(this, ResizeGrips, this.FindControl<Border>("RootCard"));
        WindowPlacement.Track(this, "JustTag.Main");

        // (!) JUST TAG carried the .maximized STYLE from the day it was built, but nothing ever set the
        // class - so its maximize left the transparent shadow frame and the rounded corners in place
        // instead of filling the screen the way "maximized" should. It implements IFramelessWindow now,
        // through the SHARED behaviour, so it cannot drift from its siblings again.
        _maximize = FramelessMaximize.Attach(this);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Fetch-what-you-see: a row that scrolls into view is read AHEAD of the background pass, so a
        // 1,200-file folder fills the screen first instead of the top of the list first (the Finder's
        // mechanic, verbatim).
        FileList.ContainerPrepared += OnFileContainerPrepared;

        // OS file-copy drag-OUT, the same gesture every other JUST list has. It was missing
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

        // Subscribed HERE and not in the constructor: the DataContext is assigned by App.axaml.cs
        // after the constructor has already returned. A run that could not analyse everything opens
        // the log by itself - the summary chip in the header says HOW MANY, and only the log can say
        // WHICH, and "which" is the part that must never be swallowed.
        if (Vm is { } tagger && !_failuresHooked)
        {
            _failuresHooked = true;
            tagger.AnalysisFailuresReported += () => OnShowAnalysisLog(this, e);
        }

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
        // pane focus to the right would dim the pane under your pointer.
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

    private void OnShowRaw(object? sender, RoutedEventArgs e) => Vm?.ShowTab(TagPane.Raw);

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
    // not walk through. Parity with the Finder is the bar - JUST TAG's navigation and search cannot
    // be a lesser version of the Finder's, since the two share the same browsing model. It does not.
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
                // play/pause on the selected row.
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
    /// places. Anything else descends.
    ///
    /// <para>Leaf-ness is decided when the folder is LISTED, not here, so this needs no round trip
    /// and the glyph on the row has already told you which of the two it is.</para>
    /// </summary>
    private void ActivateFolder(FolderRow row, bool moveFocus)
    {
        if (Vm is not { } vm) return;

        if (row.IsUp) { vm.GoUp(); return; }

        if (row.IsPlaylist) { OpenPlaylistAsList(vm, row.Path, moveFocus); return; }

        // Leaf or not is decided in the VIEW MODEL now, by the same GoTo every other way into a
        // folder goes through - startup, a drop, the picker, a crumb. It used to be decided right
        // here, and only here, which is exactly how a dropped leaf folder could be entered and then
        // remembered as the folder to reopen. alreadyInParent, because the folder
        // pane is already listing this row's parent: a leaf only has to fill the file pane.
        vm.GoTo(row.Path, alreadyInParent: true,
                whenShownAsList: moveFocus ? HandFocusToFiles : null);
    }

    /// <summary>A playlist: fill the FILE pane. The folder pane stays where it is - a set is a list
    /// of songs, not a place.</summary>
    private void OpenPlaylistAsList(TaggerViewModel vm, string path, bool moveFocus)
    {
        vm.OpenPlaylist(path);
        if (moveFocus) HandFocusToFiles();
    }

    /// <summary>Enter hands the cursor on to the file pane; a mouse click leaves it where you put it.</summary>
    private void HandFocusToFiles()
    {
        if (Vm is not { } vm) return;
        vm.Activate(TaggerViewModel.FocusPane.Files);
        FocusFiles();
    }

    private void OnCrumbClick(object? sender, RoutedEventArgs e)
    {
        // A crumb is always an ANCESTOR, so it always has something to browse into - but it goes
        // through the same door as everything else rather than relying on that staying true.
        if (sender is Control { Tag: string path }) Vm?.GoTo(path);
    }

    // The focused pane owns the header highlight, and GotFocus (which bubbles up from whatever inside
    // the pane actually took focus) is the single source of truth - so a click, TAB and a programmatic
    // focus all keep the cue honest. The EDITOR / ANALYSIS / FILTER side is a pane like the other two:
    // typing in a tag field lights ITS header, not the file list's.
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
            vm.GoTo(first);
            return;
        }

        var folder = Path.GetDirectoryName(first);
        if (string.IsNullOrEmpty(folder)) return;

        vm.GoTo(folder);
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
        var rows = (sender as ListBox)?.SelectedItems?.OfType<FileRow>().ToList() ?? [];
        vm.SetSelection(rows);

        if (_restoringSelection || Editor is not { } panel) return;

        // Already showing exactly these files? Then nothing changed that the editor cares about -
        // and re-loading would throw away edits in progress for no reason.
        if (ShowsSameFiles(vm, rows)) return;

        if (!await panel.ConfirmLeaveAsync(this))
        {
            RestoreSelection(vm.Editor.FilePath);
            return;
        }

        // MORE THAN ONE ROW turns the editor into a multi-file edit. The tags come off the ROWS
        // rather than off disk: since schema v3 the index carries them, so "do these 37 files agree
        // on their genre?" is answered without opening anything.
        if (rows.Count > 1)
            vm.Editor.LoadMany(rows.Select(r => new TagTarget(r.Path, r.Meta)).ToList());
        else if (rows.Count == 1) vm.Editor.Load(rows[0].Path);
        else vm.Editor.Clear();

        vm.SyncAnalysisTab();
    }

    /// <summary>Is the editor already pointed at exactly this set of files, in any order?</summary>
    private static bool ShowsSameFiles(TaggerViewModel vm, IReadOnlyList<FileRow> rows)
    {
        if (rows.Count > 1 || vm.Editor.IsMulti)
        {
            // A selection is compared as a SET: adding a row and removing another is a different
            // edit even when the count is the same, so the count alone cannot answer this.
            var now = vm.Editor.SelectedPaths;
            return now.Count == rows.Count
                && rows.All(r => now.Contains(r.Path));
        }

        return rows.Count == 1
            && string.Equals(rows[0].Path, vm.Editor.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    // -- The row menu ----------------------------------------------------------------------------

    /// <summary>
    /// Right-clicking a row that is NOT part of the current selection selects just it first -
    /// Explorer's rule, and the one the Finder's file pane already follows. Without it, a bulk entry
    /// would silently act on a set you cannot see any more because you are looking at the row you
    /// pointed at.
    /// </summary>
    private void OnFilesContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm || FileList is not { } list) return;
        if (e.Source is not Visual source) return;

        var row = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not FileRow clicked) return;

        if (!vm.Selected.Contains(clicked)) list.SelectedItem = clicked;
    }

    /// <summary>Play or pause the one row the menu is standing on - the same toggle Space is.</summary>
    private void OnMenuPreview(object? sender, RoutedEventArgs e)
    {
        if (Vm is { Selected: [{ } row] } vm) vm.Preview.Toggle(row.Path);
    }

    /// <summary>Open the file's folder and highlight it. A tagger is fixing files, and everything
    /// else you might do to one of them happens outside this window.</summary>
    private void OnMenuReveal(object? sender, RoutedEventArgs e)
    {
        if (Vm is { Selected: [{ } row] }) SystemFileBrowser.Reveal(row.Path);
    }

    /// <summary>
    /// The paths on the clipboard, one per line. The keyboard's version of what dragging a row out
    /// already does - handing a file to another tool, or to an agent's chat window.
    /// </summary>
    private async void OnMenuCopyPath(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Selected.Count: > 0 } vm) return;

        await SystemClipboard.CopyTextAsync(
            this, string.Join(Environment.NewLine, vm.Selected.Select(r => r.Path)));
    }

    // RAW TAGS is no longer a menu entry and no longer a window: it is the editor panel's third TAB,
    // next to TAGS and ANALYSIS (JustPlay.UI, TagEditorViewModel.Raw). The reader reaches it through
    // the editor's constructor in Program.cs - one seam, not a second door to the same room.

    /// <summary>
    /// TRANSFORM: change the text that is ALREADY in the selected files' tags - the other kind of
    /// bulk edit, and the one a freshly downloaded folder wants first.
    ///
    /// <para>The unsaved-edits question is asked FIRST, exactly as switching files asks it. Without
    /// it the panel would still be holding the pre-transform values, ticked, and the next Save would
    /// quietly write them back over what the transform just did.</para>
    ///
    /// <para>Afterwards the rows re-read their tags, and the editor is re-pointed at the same files
    /// with no tags attached - which makes it read them off DISK rather than trust what it was
    /// holding before the write.</para>
    /// </summary>
    private async void OnMenuTransform(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { Selected.Count: > 0 } vm) return;
            if (Editor is { } panel && !await panel.ConfirmLeaveAsync(this)) return;

            var rows = vm.Selected.ToList();
            var transform = vm.Editor.Transform(
                rows.Select(r => new TagTarget(r.Path, r.Meta)).ToList());

            if (!await TransformWindow.ShowAsync(this, transform)) return;

            vm.RefreshTagsFor(rows.Select(r => r.Path).ToList());
            vm.Editor.LoadMany(rows.Select(r => new TagTarget(r.Path, null)).ToList());
            vm.SyncAnalysisTab();
        }
        catch (Exception)
        {
            // `async void` on an event handler must not let anything escape - and a transform that
            // could not open its window is not worth taking the app down for.
        }
    }

    // -- Analyse ---------------------------------------------------------------------------------

    /// <summary>
    /// ANALYSE the selection: measure BPM, key, energy and loudness, and write what we measure into
    /// the files - one file or thirty, whatever is held.
    ///
    /// <para>The unsaved-edits question is asked FIRST, exactly as switching files, TRANSFORM and
    /// MOVE ask it: the analysis write goes straight to the file, so a panel still holding
    /// pre-analysis values would let the next Save put the old BPM back over the new one.</para>
    ///
    /// <para>Afterwards the editor is re-pointed at the same files with NO tags attached, which
    /// makes it read them off disk rather than trust what it was holding - the same move TRANSFORM
    /// makes after its write.</para>
    /// </summary>
    private async void OnMenuAnalyze(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { Selected.Count: > 0 } vm || vm.IsAnalysing) return;
            if (Editor is { } panel && !await panel.ConfirmLeaveAsync(this)) return;

            var rows = vm.Selected.ToList();
            await vm.AnalyzeAsync(rows);

            if (Vm is not { } after) return;
            after.Editor.LoadMany(rows.Select(r => new TagTarget(r.Path, null)).ToList());
            after.SyncAnalysisTab();
        }
        catch (Exception)
        {
            // `async void` on an event handler must not let anything escape - and a batch that
            // could not start is not worth taking the app down for.
        }
    }

    /// <summary>Stop the run after the files in flight. What already landed stays landed.</summary>
    private void OnStopAnalysis(object? sender, RoutedEventArgs e) => Vm?.CancelAnalysis();

    /// <summary>
    /// The SHARED event-log window (JustPlay.UI) on JUST TAG's log - opened by the summary chip, and
    /// by itself when a run left files behind. Every failure is in there by name; this app had no
    /// such channel before, so a file the analyzer could not read had nowhere to be reported.
    /// </summary>
    private void OnShowAnalysisLog(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { } vm) return;

            if (_log is { } open) { open.Activate(); return; }

            var log = new LogWindow { DataContext = vm.Log };
            WindowPlacement.Track(log, "JustTag.Log");
            log.Closed += (_, _) => _log = null;
            _log = log;
            log.Show(this);
        }
        catch (Exception)
        {
            // A window that could not open is not worth taking the app down for.
        }
    }

    /// <summary>The log window, while it is up - so a second click raises it instead of stacking a
    /// second copy on top of the first.</summary>
    private LogWindow? _log;

    /// <summary>OnLoaded can run more than once (Avalonia re-raises it when a window is re-attached),
    /// and a second subscription would open the log twice for one failed run.</summary>
    private bool _failuresHooked;

    /// <summary>
    /// Re-read from disk WHAT IS ON SCREEN - which is not always the folder. The pane also shows a
    /// playlist's tracks, or the tracks of a leaf folder you did not navigate into; the view model
    /// remembers which in its source path, and its own Refresh re-opens that.
    ///
    /// <para>This used to call <c>Open(Folder)</c>, and <c>Open</c> CLEARS the source path - so
    /// refreshing while standing in a leaf folder's tracks re-opened the PARENT, which usually holds
    /// no audio of its own, and the list came back empty. One refresh, one implementation.</para>
    /// </summary>
    private void OnMenuRefresh(object? sender, RoutedEventArgs e) => Vm?.Refresh();

    private async void OnMenuMove(object? sender, RoutedEventArgs e) =>
        await OrganiseAsync(OrganiseAction.Move);

    private async void OnMenuCopyTo(object? sender, RoutedEventArgs e) =>
        await OrganiseAsync(OrganiseAction.Copy);

    private async void OnMenuDelete(object? sender, RoutedEventArgs e) =>
        await OrganiseAsync(OrganiseAction.Delete);

    /// <summary>
    /// MOVE / COPY / DELETE the selection. Everything that decides anything is in the shared
    /// window; this only hands it the three things that are JUST TAG's to supply.
    ///
    /// <para>The unsaved-edits question is asked FIRST, exactly as switching files and TRANSFORM ask
    /// it - the panel would otherwise still be holding values for a file that is no longer there.
    /// </para>
    ///
    /// <para><b>Releasing the preview</b> is the callback: a file JUST TAG is playing has an open
    /// handle and Windows will not move or delete it. It runs on the UI thread, before a byte is
    /// touched. (JUST TAG has no deferred tag-write queue to drain - it releases the file and writes
    /// immediately, see the executor in <c>Program.ConfigureServices</c>.)</para>
    ///
    /// <para><b>The index</b> is reconciled afterwards so a moved track is never lost from the
    /// library - and only for folders that are already indexed.</para>
    /// </summary>
    private async Task OrganiseAsync(OrganiseAction action)
    {
        try
        {
            if (Vm is not { Selected.Count: > 0 } vm) return;
            if (Editor is { } panel && !await panel.ConfirmLeaveAsync(this)) return;

            var organise = new OrganiseViewModel(
                action,
                [.. vm.Selected.Select(r => r.Path)],
                SystemRecycleBin.Instance,
                new LibraryOrganiseSync(Program.Services.GetRequiredService<IMetadataReader>()),
                paths => { foreach (var path in paths) vm.Preview.ReleaseIfPlaying(path); });

            // Only re-list when something actually happened on disk. Refresh (not Open) because the
            // pane may be showing a playlist or a leaf folder's tracks - see OnMenuRefresh.
            if (await OrganiseWindow.ShowAsync(this, organise)) vm.Refresh();
        }
        catch (Exception)
        {
            // `async void` on an event handler must not let anything escape - and an operation that
            // could not open its window is not worth taking the app down for.
        }
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

            if (picked?.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path) Vm?.GoTo(path);
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
