using System;
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
using JustPlay.UI.Controls;
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

        // Queue key handling MUST be registered with handledEventsToo:true. Avalonia's ListBoxItem
        // marks Key.Enter (and Key.Space) as Handled during the bubble pass BEFORE a plain XAML
        // KeyDown handler runs — ListBoxItem.OnKeyDown → SelectingItemsControl.UpdateSelectionFromEvent,
        // and ItemSelectionEventTriggers.ShouldTriggerSelection returns true for Space/Enter and sets
        // e.Handled = true (verified against Avalonia 12.0.3 source). A normal (handledEventsToo:false)
        // handler — which is what KeyDown="..." in XAML installs — therefore never sees Enter, so the
        // highlighted row's PlayTrack was silently swallowed. Registering here with handledEventsToo:true
        // lets OnTrackListKeyDown observe the already-handled Enter and start the track. The XAML
        // KeyDown attribute is removed so this is the SOLE registration (no double-fire on unhandled
        // keys like Delete / type-ahead).
        this.FindControl<ListBox>("TrackList")?.AddHandler(
            InputElement.KeyDownEvent, OnTrackListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && WindowChrome.IsInteractive(v)) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    // Title-bar brand → open the themed About dialog (moved here from the Tweaks panel).
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            new AboutWindow().ShowDialog(owner);
    }

    // Title-bar update badge → show the update dialog, then install / ignore / dismiss.
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

    // ── Multi-select queue → keep the VM's SelectedTracks in sync ──────────
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not ListBox lb) return;
        vm.SelectedTracks.Clear();
        foreach (var t in lb.SelectedItems!.OfType<TrackViewModel>())
            vm.SelectedTracks.Add(t);
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

    /// <summary>A-Z / 0-9 key → its character for type-ahead, else null.</summary>
    private static char? CharForKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        _ => null,
    };

    // Clickable table headers → sort by that column (Tag carries the column name).
    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control { Tag: string col })
            vm.SortByColumn(col);
    }

    // Right-click: if the clicked row isn't part of the current multi-selection, select just it
    // (standard explorer behaviour) so the menu's bulk actions apply to what the user pointed at.
    // ContextTarget — the anchor for per-field entries — is set only when exactly one row ends up
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

    /// <summary>Walk up from the right-clicked element to find which value cell it sits in — the
    /// BPM/Key/Energy cells carry a Tag. Null when the click was on the title or empty row space.</summary>
    private static string? ClickedField(Visual? from)
    {
        for (var v = from; v is not null; v = v.GetVisualParent())
            if (v is Control { Tag: string tag } && tag is "Bpm" or "Key" or "Energy")
                return tag;
        return null;
    }

    // ── Streaming cap-button right-click menu ──────────────────────────────
    // Built fresh on every open so the items track the live broadcast state and the
    // current radio list: "Disconnect" while on air, otherwise one "Connect to <name>"
    // per configured radio (or "Add a radio…" when none), then a settings shortcut.
    // Done in code rather than via XAML ItemsSource so each item's Click wiring stays
    // trivial — no per-item Command binding on generated MenuItems.
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
            menu.Items.Add(StreamMenuItem("Add a radio…", vm.OpenStreaming));
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
        menu.Items.Add(StreamMenuItem("Streaming settings…", vm.OpenStreaming));
    }

    private static MenuItem StreamMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // ── Playlist open / export (M3U8) ──────────────────────────────────────
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
    // .m3u8 that lists them — one file to share / upload / carry on a stick into other DJ software.
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

        // Name the .m3u8 inside the zip after the archive the user chose ("Summer Set.zip" → "Summer Set.m3u8").
        var name = Path.GetFileNameWithoutExtension(path);
        await RunTransfer(owner, vm, $"{name}  ·  ZIP",
            (progress, ct) => vm.ExportPlaylistZipAsync(path, name, progress, ct));
    }

    // Export the set as loose files copied into a folder (audio numbered in set order + the .m3u8).
    // We pick a destination, ASK what the set folder should be called, and create that subfolder inside
    // the chosen location — so the set stays self-contained rather than dumping loose files.
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

        await RunTransfer(owner, vm, $"{name}  ·  folder",
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
        catch (OperationCanceledException) { /* user cancelled — Core already cleaned up the partial output */ }
        catch { /* swallow other I/O errors; the dialog just closes */ }
        finally
        {
            win.Close(); // no-op if the window was already dismissed / closed by Cancel
        }
    }

    /// <summary>Default export name: JustPlay_YEAR-MONTH-DAY_HH-MM (no extension — pickers add it).</summary>
    private static string DefaultSetName() => $"JustPlay_{DateTime.Now:yyyy-MM-dd_HH-mm}";

    // ── PRE-CUE: "Add files…" button ──────────────────────────────────────────
    // Opens a multi-select file picker then hands the chosen paths to the ViewModel,
    // which builds TrackViewModel wrappers and appends them to PreCueTracks.
    // Lives in code-behind because file pickers require the window's StorageProvider.
    private async void OnAddPreCueFilesClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add tracks to audition list",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio files")
                {
                    Patterns = ["*.mp3", "*.flac", "*.wav", "*.aiff", "*.aif", "*.ogg", "*.m4a", "*.opus", "*.wma"],
                }
            ],
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .OfType<string>();

        await vm.AddPreCuePathsAsync(paths);
    }

    // Window min/max/close now live in the shared WindowControls control.
}
