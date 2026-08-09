using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JustPlay.App.Controls;
using JustPlay.Core.Abstractions;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using JustPlay.UI.ViewModels;
using JustPlay.UI.Views;
using JustPlay.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App.Views;

public partial class MaxView : UserControl
{
    public MaxView()
    {
        InitializeComponent();
        // Drag the frameless window by clicking the chrome bar.
        this.FindControl<Border>("ChromeBar")?.AddHandler(PointerPressedEvent, OnChromePressed, RoutingStrategies.Tunnel);

        // macOS: the caption buttons sit LEFT, so the top-right corner belongs to the update
        // badge while it is visible, otherwise to the log button. "corner" (JustStyles) rounds
        // the hover to the card radius so it stays flush inside the rounded window edge.
        if (OperatingSystem.IsMacOS())
        {
            void SetCorner()
            {
                bool up = (DataContext as MainWindowViewModel)?.Update.IsAvailable == true;
                UpdateBadge.Classes.Set("corner", up);
                LogBtn.Classes.Set("corner", !up);
            }
            DataContextChanged += (_, _) =>
            {
                if (DataContext is MainWindowViewModel vm)
                    vm.Update.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(UpdateViewModel.IsAvailable))
                            Dispatcher.UIThread.Post(SetCorner);
                    };
                SetCorner();
            };
            SetCorner();
        }

        // Queue key handling MUST be registered with handledEventsToo:true. Avalonia's ListBoxItem
        // marks Key.Enter (and Key.Space) as Handled during the bubble pass BEFORE a plain XAML
        // KeyDown handler runs - ListBoxItem.OnKeyDown -> SelectingItemsControl.UpdateSelectionFromEvent,
        // and ItemSelectionEventTriggers.ShouldTriggerSelection returns true for Space/Enter and sets
        // e.Handled = true (verified against Avalonia 12.0.3 source). A normal (handledEventsToo:false)
        // handler - which is what KeyDown="..." in XAML installs - therefore never sees Enter, so the
        // highlighted row's PlayTrack was silently swallowed. Registering here with handledEventsToo:true
        // lets OnTrackListKeyDown observe the already-handled Enter and start the track. The XAML
        // KeyDown attribute is removed so this is the SOLE registration (no double-fire on unhandled
        // keys like Delete / type-ahead).
        this.FindControl<ListBox>("TrackList")?.AddHandler(
            InputElement.KeyDownEvent, OnTrackListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // Row drag, combined mode (option A): drag INSIDE the list = hand-reorder
        // the queue (the set-building gesture - accent insertion line, edge autoscroll, multi-select
        // moves the block); drag OUT sideways = the OS file-copy drag to Explorer/Traktor/an
        // AI-agent chat (N24). See RowDragBehavior for the Avalonia-12-source-verified mechanics.
        if (this.FindControl<ListBox>("TrackList") is { } trackList &&
            this.FindControl<Border>("QueueDropIndicator") is { } dropIndicator)
        {
            RowDragBehavior.Attach(trackList,
                dc => (dc as TrackViewModel)?.Model.FilePath,
                new RowReorder
                {
                    Indicator = dropIndicator,
                    // Dragging while a column sort is active is allowed and ADOPTS the sorted order
                    // as the new hand order (sort glyph clears - MoveTracks handles it). Implicit
                    // beats a dead gesture - a hidden interaction nobody would find on their own;
                    // sort-then-hand-tune is a real set-building flow.
                    Move = (items, insertIndex) =>
                    {
                        if (DataContext is MainWindowViewModel vm)
                            vm.MoveTracks(items.OfType<TrackViewModel>().ToList(), insertIndex);
                    },
                });
        }
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e) =>
        // Drag from empty chrome, DOUBLE-click to maximize/restore - one shared gesture.
        WindowChrome.HandlePress((TopLevel.GetTopLevel(this) as Window), e);

    // Title-bar brand -> open the SHARED themed About dialog (JustPlay.UI), parameterized with
    // JUST PLAY's name / tagline / version / glyph so every JUST app's About is identical.
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            var about = new JustPlay.UI.Views.AboutWindow(new JustPlay.UI.Views.AboutInfo(
                AppName: "JustPlay",
                Tagline: "Key-aware DJ music player",
                Version: $"Version {AppInfo.Version}",
                Glyph: BrandGlyphs.Play));
            about.ShowDialog(owner);
        }
    }

    // Transport spectrum button -> open the live analyzer (NON-modal: keep playing / controlling while
    // it's open). Single instance - a second click just re-focuses the existing window.
    private JustPlay.UI.Views.SpectrumWindow? _spectrumWindow;
    private void OnSpectrumClick(object? sender, RoutedEventArgs e)
    {
        if (_spectrumWindow is not null) { _spectrumWindow.Activate(); return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        // The shared analyzer (JustPlay.UI) takes an ISpectrumSource - our IAudioEngine implements it.
        _spectrumWindow = new JustPlay.UI.Views.SpectrumWindow(Program.Services?.GetService<IAudioEngine>());
        _spectrumWindow.Closed += (_, _) => _spectrumWindow = null;
        _spectrumWindow.Show(owner);
    }

    // Event-log button -> open the shared LogWindow (JustPlay.UI). Single instance, NON-modal; opening clears
    // the unread marker. DataContext = the VM's shared EventLog feed (file-lock / tag-write failures land there).
    private JustPlay.UI.Views.LogWindow? _logWindow;
    private void OnOpenLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (_logWindow is not null) { _logWindow.Activate(); vm.EventLog.MarkRead(); return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        _logWindow = new JustPlay.UI.Views.LogWindow { DataContext = vm.EventLog };
        JustPlay.UI.Behaviors.WindowPlacement.Track(_logWindow, "JustPlay.Log"); // shared window: opener sets the key
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show(owner);
        vm.EventLog.MarkRead();
    }

    // Title-bar update badge -> show the update dialog, then install / ignore / dismiss.
    private async void OnUpdateBadge(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var http = JustPlay.App.Program.Services.GetRequiredService<System.Net.Http.HttpClient>();
        await JustPlay.App.Updates.UpdateFlow.ShowAndApplyAsync(owner, vm.Update, http);
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm
            && this.FindControl<ListBox>("TrackList")?.SelectedItem is TrackViewModel track)
        {
            vm.PlayTrackCommand.Execute(track);
        }
    }

    // -- Multi-select queue -> keep the VM's SelectedTracks in sync ----------
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not ListBox lb) return;
        vm.SelectedTracks.Clear();
        foreach (var t in lb.SelectedItems!.OfType<TrackViewModel>())
            vm.SelectedTracks.Add(t);

        // The tag editor follows the CURSOR. Deliberately only here: auto-advance at the end of a
        // track changes what is PLAYING, not what is selected, and must never retarget the editor
        // mid-edit. Multi-select points it at the row the cursor landed on.
        if (_tagEditor is not null)
            _ = _tagEditor.SetTargetAsync((lb.SelectedItem as TrackViewModel)?.Model.FilePath);
    }

    // -- The shared, always-on-top tag editor ----------------------------------------------------
    // Same window and same body the PRE CUE FINDER opens, and the one JUST TAG docks as its sidebar.

    private TagEditorWindow? _tagEditor;

    private void OnEditTags(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var targets = TagTargets(vm);
        if (targets.Count == 0) return;

        if (_tagEditor is null)
        {
            var editor = vm.CreateTagEditor();
            editor.Saved += vm.RefreshTagsFor;
            _tagEditor = TagEditorWindow.Open(owner, null, editor);
            // A closed Avalonia window cannot be shown again - drop the reference so the next
            // "Edit tags..." builds a fresh one instead of raising a dead window.
            _tagEditor.Closed += (_, _) => _tagEditor = null;
        }
        else
        {
            _tagEditor.Activate();
        }

        _ = _tagEditor.SetSelectionAsync(targets);
    }

    /// <summary>
    /// What the editor should open on. The SELECTION when there is one - right-clicking inside a
    /// selection means "these", which is what every other list in the suite already does - and the
    /// right-clicked row alone otherwise.
    /// <para>The tags ride along from the rows: the queue reads them when a track is added, so
    /// answering "do these agree on their genre?" opens no file.</para>
    /// </summary>
    private static IReadOnlyList<TagTarget> TagTargets(MainWindowViewModel vm)
    {
        if (vm.SelectedTracks.Count > 0)
            return vm.SelectedTracks.Select(t => new TagTarget(t.Model.FilePath, t.Model.Metadata))
                                    .ToList();

        return vm.ContextTarget is { } one
            ? [new TagTarget(one.Model.FilePath, one.Model.Metadata)]
            : [];
    }

    // Queue keyboard behaviour: Delete removes, Enter plays, a typed letter/digit jumps to the next
    // matching title (type-ahead). Arrow/Home/End/PageUp-Down come free from the ListBox.
    private void OnTrackListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not ListBox lb) return;

        switch (e.Key)
        {
            case Key.Delete:
                DeleteSelected(vm, lb);
                e.Handled = true;
                return;

            case Key.Enter:
                if (lb.SelectedItem is TrackViewModel play)
                {
                    vm.PlayTrackCommand.Execute(play);
                    e.Handled = true;
                }
                return;
        }

        // Type-ahead: a bare letter/digit selects the next track whose title starts with it.
        if (e.KeyModifiers == KeyModifiers.None && CharForKey(e.Key) is { } ch && vm.Tracks.Count > 0)
        {
            var start = lb.SelectedIndex;
            for (var k = 1; k <= vm.Tracks.Count; k++)
            {
                var idx = (start + k) % vm.Tracks.Count;
                if (vm.Tracks[idx].Title.StartsWith(ch.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    SelectAndFocus(lb, idx);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private static void DeleteSelected(MainWindowViewModel vm, ListBox lb)
    {
        if (vm.SelectedTracks.Count == 0) return;
        var firstIdx = vm.SelectedTracks
            .Select(t => vm.Tracks.IndexOf(t))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        vm.RemoveSelected();
        if (vm.Tracks.Count == 0) return;
        SelectAndFocus(lb, Math.Min(firstIdx, vm.Tracks.Count - 1));
    }

    private static void SelectAndFocus(ListBox lb, int index)
    {
        lb.SelectedIndex = index;
        lb.ScrollIntoView(index);
        lb.Focus();
        // The container may not be realized until after layout; focus it once it exists.
        Dispatcher.UIThread.Post(() => (lb.ContainerFromIndex(index) as Control)?.Focus(),
            DispatcherPriority.Background);
    }

    /// <summary>A-Z / 0-9 key -> its character for type-ahead, else null.</summary>
    private static char? CharForKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        _ => null,
    };

    // Clickable table headers -> sort by that column (Tag carries the lowercase TrackColumns id). The
    // asc -> desc -> off cycle lives in the shared Columns; its SortRequested re-orders the queue.
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control { Tag: string col })
            vm.Columns.SortBy(col);
    }

    // Right-click: if the clicked row isn't part of the current multi-selection, select just it
    // (standard explorer behaviour) so the menu's bulk actions apply to what the user pointed at.
    // ContextTarget - the anchor for per-field entries - is set only when exactly one row ends up
    // selected, so single-field writes never appear (ambiguously) during a multi-selection.
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var lb = this.FindControl<ListBox>("TrackList");
        if (lb is null) return;

        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as TrackViewModel;
        if (row is null) { vm.ContextTarget = null; vm.ContextField = null; return; }

        if (!lb.SelectedItems!.Contains(row))
        {
            lb.SelectedItems.Clear();
            lb.SelectedItems.Add(row);
        }
        vm.ContextTarget = lb.SelectedItems.Count == 1 ? row : null;
        vm.ContextField = ClickedField(e.Source as Visual);
        vm.RefreshMenuState(); // fresh enable/disable for Write / Fill / Analyze as the menu opens
    }

    /// <summary>Walk up from the right-clicked element to find which value cell it sits in - the
    /// BPM/Key/Energy cells carry a Tag. Null when the click was on the title or empty row space.</summary>
    private static string? ClickedField(Visual? from)
    {
        for (var v = from; v is not null; v = v.GetVisualParent())
            if (v is Control { Tag: string tag } && tag is "Bpm" or "Key" or "Energy")
                return tag;
        return null;
    }

    // -- Streaming cap-button right-click menu ------------------------------
    // Built fresh on every open so the items track the live broadcast state and the
    // current radio list: "Disconnect" while on air, otherwise one "Connect to <name>"
    // per configured radio (or "Add a radio..." when none), then a settings shortcut.
    // Done in code rather than via XAML ItemsSource so each item's Click wiring stays
    // trivial - no per-item Command binding on generated MenuItems.
    private void OnStreamMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || DataContext is not MainWindowViewModel vm) return;
        menu.Items.Clear();

        if (vm.IsConnectedOrConnecting)
        {
            menu.Items.Add(StreamMenuItem("Disconnect", () => _ = vm.DisconnectBroadcastAsync()));
        }
        else if (vm.StreamServers.Count == 0)
        {
            menu.Items.Add(StreamMenuItem("Add a radio...", vm.OpenStreaming));
        }
        else
        {
            foreach (var server in vm.StreamServers)
            {
                var target = server; // capture per iteration for the closure
                var label = string.IsNullOrWhiteSpace(target.Name) ? target.Host : target.Name;
                menu.Items.Add(StreamMenuItem($"Connect to {label}", () => _ = vm.ConnectBroadcastAsync(target)));
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(StreamMenuItem("Streaming settings...", vm.OpenStreaming));
    }

    private static MenuItem StreamMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // -- Playlist open / export (M3U8) --------------------------------------
    // Dialogs live in code-behind (they need the window's StorageProvider). "Open" REPLACES the
    // queue with the playlist's set; "Export" writes the current order to a .m3u8.
    private async void OnOpenPlaylist(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open playlist",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Playlists") { Patterns = ["*.m3u8", "*.m3u"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await vm.LoadPlaylistAsync(path);
    }

    private async void OnExportPlaylist(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.Tracks.Count == 0) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export playlist",
            SuggestedFileName = DefaultSetName(),
            DefaultExtension = "m3u8",
            FileTypeChoices = [new FilePickerFileType("M3U8 playlist") { Patterns = ["*.m3u8"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
            await vm.ExportPlaylistM3uAsync(path);
    }

    // Export the whole set as a self-contained .zip: the audio files (numbered in set order) plus an
    // .m3u8 that lists them - one file to share / upload / carry on a stick into other DJ software.
    private async void OnExportZip(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.Tracks.Count == 0) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export set as ZIP (audio + playlist)",
            SuggestedFileName = DefaultSetName(),
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        // Name the .m3u8 inside the zip after the archive the user chose ("Summer Set.zip" -> "Summer Set.m3u8").
        var name = Path.GetFileNameWithoutExtension(path);
        await RunTransfer(owner, vm, $"{name}  -  ZIP",
            (progress, ct) => vm.ExportPlaylistZipAsync(path, name, progress, ct));
    }

    // Export the set as loose files copied into a folder (audio numbered in set order + the .m3u8).
    // We pick a destination, ASK what the set folder should be called, and create that subfolder inside
    // the chosen location - so the set stays self-contained rather than dumping loose files.
    private async void OnExportFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.Tracks.Count == 0) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to copy the set",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } parent) return;

        var name = await InputDialog.AskAsync(owner, "Name the set folder", DefaultSetName());
        if (string.IsNullOrWhiteSpace(name)) return;

        await RunTransfer(owner, vm, $"{name}  -  folder",
            (progress, ct) => vm.ExportPlaylistFolderAsync(Path.Combine(parent, name), name, progress, ct));
    }

    // Open the progress dialog, run the export with progress + cancellation wired to it, then close the
    // dialog when the Task settles. Dismiss closes the window early but the Task runs on; Cancel trips
    // the token and the Core writer removes its partial output.
    private static async Task RunTransfer(Window owner, MainWindowViewModel vm, string target,
        Func<IProgress<(int done, int total)>, CancellationToken, Task<int>> run)
    {
        var tvm = new TransferViewModel { Target = target };
        tvm.Report(0, vm.Tracks.Count); // initial estimate; Core reports the exact total on the first file
        var win = new TransferWindow { DataContext = tvm };
        win.Show(owner);

        var progress = new Progress<(int done, int total)>(p => tvm.Report(p.done, p.total));
        try
        {
            await run(progress, tvm.Token);
        }
        catch (OperationCanceledException) { /* user cancelled - Core already cleaned up the partial output */ }
        catch { /* swallow other I/O errors; the dialog just closes */ }
        finally
        {
            win.Close(); // no-op if the window was already dismissed / closed by Cancel
        }
    }

    /// <summary>Default export name: JustPlay_YEAR-MONTH-DAY_HH-MM (no extension - pickers add it).</summary>
    private static string DefaultSetName() => $"JustPlay_{DateTime.Now:yyyy-MM-dd_HH-mm}";

    // (The pre-cue "Load track..." file picker was removed 2026-07-03 - cueing happens from the
    // track list via the row context menu; see LoadPreCueTrackAsync in MainWindowViewModel.)

    /// <summary>Open (or surface) the PRE CUE FINDER - the keyboard-first library audition
    /// explorer window. Singleton window + singleton VM: reopening resumes where she left off.</summary>
    private void OnOpenFinderClicked(object? sender, RoutedEventArgs e)
        => PreCueFinderWindow.ShowOrActivate();

    // Window min/max/close now live in the shared WindowControls control.
}
