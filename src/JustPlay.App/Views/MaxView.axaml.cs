using System;
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

namespace JustPlay.App.Views;

public partial class MaxView : UserControl
{
    public MaxView()
    {
        InitializeComponent();
        // Drag the frameless window by clicking the chrome bar.
        this.FindControl<Border>("ChromeBar")?.AddHandler(PointerPressedEvent, OnChromePressed, RoutingStrategies.Tunnel);
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && WindowChrome.IsInteractive(v)) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
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

    // ── Tab swap (UP NEXT / LYRICS) ────────────────────────────────────────
    private void OnUpNextTabClick(object? sender, RoutedEventArgs e) => ShowTab(queue: true);
    private void OnLyricsTabClick(object? sender, RoutedEventArgs e) => ShowTab(queue: false);


    private void ShowTab(bool queue)
    {
        var qp = this.FindControl<Panel>("QueuePanel");
        var lp = this.FindControl<StackPanel>("LyricsPanel");
        var qt = this.FindControl<Button>("UpNextTab");
        var lt = this.FindControl<Button>("LyricsTab");
        if (qp is not null) qp.IsVisible = queue;
        if (lp is not null) lp.IsVisible = !queue;
        qt?.Classes.Set("active", queue);
        lt?.Classes.Set("active", !queue);
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
            SuggestedFileName = "JustPlay set",
            DefaultExtension = "m3u8",
            FileTypeChoices = [new FilePickerFileType("M3U8 playlist") { Patterns = ["*.m3u8"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
            await vm.ExportPlaylistM3uAsync(path);
    }

    // Window min/max/close now live in the shared WindowControls control.
}
