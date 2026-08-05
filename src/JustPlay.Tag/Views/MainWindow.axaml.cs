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
using JustPlay.Tag.ViewModels;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.Theming;
using JustPlay.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Tag.Views;

/// <summary>
/// JUST TAG's window: the PRE CUE FINDER's frameless card and chrome, an mp3tag-shaped body
/// (folders · files · editor), and the SHARED <see cref="TagEditorPanel"/> as the docked sidebar.
///
/// <para>The code-behind holds only what needs a <see cref="TopLevel"/> (the folder picker, the
/// dialogs) and the clicks that move between folders. Every decision about a FILE lives in the
/// shared view model, so JUST TAG and the floating editor in JUST PLAY cannot drift apart.</para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Guards the selection handler while we put the selection BACK after a cancelled switch —
    /// without it, restoring the row would re-enter the handler and ask about the same unsaved
    /// edits a second time.
    /// </summary>
    private bool _restoringSelection;

    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY — re-setting it here trips Avalonia's
        // macOS opaque fallback (black surround); measured 2026-07-31.

        FramelessResizeBehavior.Attach(this, ResizeGrips);
        WindowPlacement.Track(this, "JustTag.Main");

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private TaggerViewModel? Vm => DataContext as TaggerViewModel;

    // Editor / FileList / ResizeGrips come from Avalonia's x:Name generator — declaring them here
    // as well is a CS0102 collision, not a convenience.

    // ── Browsing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single click on a folder row OPENS it — ".." goes up, a name goes in. One click is one
    /// level, the same rule the Finder's folder pane follows.
    /// </summary>
    private void OnFolderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: FolderRow row } || Vm is not { } vm) return;

        // A playlist is a virtual folder: clicking it fills the FILE pane with its tracks, it does
        // not descend anywhere. Same rule as the Finder.
        if (row.IsPlaylist) vm.OpenPlaylist(row.Path);
        else vm.Open(row.Path);
    }

    private void OnClearSearch(object? sender, RoutedEventArgs e) => Vm?.ClearSearch();

    private void OnToggleSecond(object? sender, RoutedEventArgs e) => Vm?.ToggleSecond();

    private void OnShowEditor(object? sender, RoutedEventArgs e) => Vm?.ShowTab(filter: false);

    private void OnShowFilter(object? sender, RoutedEventArgs e) => Vm?.ShowTab(filter: true);

    // ── Preview ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Play or pause whatever the file list is pointing at.</summary>
    private void PreviewSelected()
    {
        if (Vm is not { } vm || FileList.SelectedItem is not FileRow row) return;
        vm.Preview.Toggle(row.Path);
    }

    private void OnPreviewToggle(object? sender, RoutedEventArgs e) => PreviewSelected();

    /// <summary>A double-click starts listening — the gesture that means "open this" everywhere.</summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e) => PreviewSelected();

    /// <summary>
    /// SPACE plays and pauses, and it is swallowed here so the list does not also treat it as a
    /// selection key. It must NOT fire while a tag field has focus — space is a character there.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && FocusManager?.GetFocusedElement() is not TextBox)
        {
            PreviewSelected();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnCrumbClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string path }) Vm?.Open(path);
    }

    private void OnFoldersGotFocus(object? sender, RoutedEventArgs e) => Vm?.Activate(folders: true);

    private void OnFilesGotFocus(object? sender, RoutedEventArgs e) => Vm?.Activate(folders: false);

    // ── Drop a folder ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A folder dropped anywhere on the window opens it. Dropping FILES opens the folder they are
    /// in and selects the first one — the alternative (refusing the drop) would be pedantic about a
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
    /// retargets, and a Cancel puts the selection back where it was — in a docked sidebar, a list
    /// and an editor showing two different files would be a lie about what Save is about to write.
    /// </summary>
    private async void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || Vm is not { } vm || Editor is not { } panel) return;

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
            // Cancelled, or a provider that refused — leave the browser where it was. `async void`
            // on an event handler must not let an exception escape.
        }
    }

    // ── Chrome ──────────────────────────────────────────────────────────────────────────────────

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    /// <summary>Chrome gear → the frameless Settings card (theme + ID3 write mode). Shared singleton
    /// view model, so a theme switch repaints the whole suite immediately.</summary>
    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow
        {
            DataContext = Program.Services.GetRequiredService<SettingsViewModel>(),
        };
        _ = settings.ShowDialog(this);
    }

    /// <summary>Brand mark (top-left) → the SHARED About dialog, parameterised with JUST TAG's name
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
