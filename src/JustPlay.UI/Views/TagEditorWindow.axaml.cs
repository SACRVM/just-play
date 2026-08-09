using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.ViewModels;

namespace JustPlay.UI.Views;

/// <summary>
/// The floating, always-on-top tag editor - shared by JUST PLAY and the PRE CUE FINDER, and the
/// same <see cref="TagEditorPanel"/> body JUST TAG will dock as its sidebar.
///
/// <para><b>It follows the SELECTION, not playback.</b> The host calls
/// <see cref="SetTargetAsync"/> when the user picks a different row; the track ending and the next
/// one loading must NOT call it. Retargeting on playback would pop a save prompt in the middle of
/// typing, triggered by an event the user did not cause.</para>
///
/// <para>When there are unsaved edits and the user answers <see cref="SaveChoice.Cancel"/>, the
/// editor KEEPS its current file - the list selection has already moved, and that is fine: the file
/// being edited is named in the panel's FILE NAME box, so the two being temporarily out of step is
/// visible rather than silent. Yanking the list's selection back would be worse.</para>
/// </summary>
public partial class TagEditorWindow : Window
{
    private bool _forceClose;
    private readonly bool _hasRememberedSize;
    private bool _autoSized;

    /// <summary>Parameterless ctor for the XAML previewer.</summary>
    public TagEditorWindow() : this(null) { }

    public TagEditorWindow(TagEditorViewModel? editor)
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY - re-setting it here trips Avalonia's
        // macOS opaque fallback (black surround). Measured 2026-07-31: Win32 fixes a window's
        // transparency at CREATION, so a later assignment does nothing anyway.

        Editor = editor ?? throw new ArgumentNullException(nameof(editor),
            "The tag editor needs its view model; the parameterless ctor exists for the previewer only.");

        DataContext = Editor;
        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!,
                                      this.FindControl<Border>("DialogCard"));
        // ".v2" retires the sizes remembered while the height was hard-coded - those were never a
        // choice, they were a wrong default that got persisted, and keeping them would mean the
        // measure-once below never runs on the machines that need it most. From here on, a size in
        // this store IS a choice and is left alone.
        _hasRememberedSize = WindowPlacement.Track(this, "Just.TagEditor.v2");
    }

    /// <summary>The shared editor state. The host reads <see cref="TagEditorViewModel.Saved"/> to
    /// refresh its own row for a file that was just written.</summary>
    public TagEditorViewModel Editor { get; }

    private TagEditorPanel? Body => this.FindControl<TagEditorPanel>("Panel");

    /// <summary>
    /// Point the editor at another file, asking about unsaved edits first.
    /// Returns false when the user chose to stay on the current file.
    /// </summary>
    public async Task<bool> SetTargetAsync(string? path)
    {
        if (!Editor.IsMulti &&
            string.Equals(Editor.FilePath, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (Body is { } body && !await body.ConfirmLeaveAsync(this)) return false;

        if (path is null) Editor.Clear();
        else { Editor.Load(path); FitHeightToContentOnce(); }
        return true;
    }

    /// <summary>
    /// Point the editor at a whole SELECTION - the floating window's half of multi-file editing, so
    /// the PRE CUE FINDER gets it from the same panel JUST TAG docks.
    ///
    /// <para>One file falls through to <see cref="SetTargetAsync"/>, so the ordinary case has no
    /// second path. A selection is compared as a SET: swapping one row for another is a different
    /// edit even when the count is unchanged, and the count alone cannot see that.</para>
    /// </summary>
    public async Task<bool> SetSelectionAsync(IReadOnlyList<TagTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count <= 1) return await SetTargetAsync(targets.Count == 1 ? targets[0].Path : null);

        var now = Editor.SelectedPaths;
        if (Editor.IsMulti && now.Count == targets.Count && targets.All(t => now.Contains(t.Path)))
            return true;

        if (Body is { } body && !await body.ConfirmLeaveAsync(this)) return false;

        Editor.LoadMany(targets);
        // A tab that vanishes under you has to leave you somewhere.
        if (Body is { } panel) panel.ShowAnalysis = false;
        FitHeightToContentOnce();
        return true;
    }

    /// <summary>
    /// Make the window exactly as tall as the panel wants, once, the first time a file is in it.
    /// <para>The alternative is a hard-coded <c>Height</c>, and that number goes stale the moment a
    /// field is added - it did, twice in one afternoon (2026-08-05). Letting the layout answer the
    /// question means it cannot be wrong again.</para>
    /// <para>It runs only when there is NO remembered size: once you have resized the editor
    /// yourself, that is the answer, and re-measuring would throw your choice away on every open.
    /// Afterwards <see cref="Window.SizeToContent"/> goes back to Manual so switching to a taller
    /// tab scrolls instead of making the window jump.</para>
    /// </summary>
    private void FitHeightToContentOnce()
    {
        if (_autoSized || _hasRememberedSize) return;
        _autoSized = true;

        SizeToContent = SizeToContent.Height;
        Dispatcher.UIThread.Post(() =>
        {
            SizeToContent = SizeToContent.Manual;
            WindowPlacement.EnsureVisible(this);   // a very long panel must not grow off the screen
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Open the editor, or bring the existing one forward - a second instance would give one file
    /// two independent sets of unsaved edits. Hosts keep the returned window in a field and clear
    /// that field on <see cref="Window.Closed"/>: a closed Avalonia window cannot be shown again.
    /// Call <see cref="SetTargetAsync"/> afterwards to point it at a file.
    /// </summary>
    public static TagEditorWindow Open(Window owner, TagEditorWindow? existing, TagEditorViewModel editor)
    {
        if (existing is not null)
        {
            existing.Activate();
            return existing;
        }

        var window = new TagEditorWindow(editor);
        window.Show(owner);
        return window;
    }

    // Drag the frameless card from the chrome bar (but not from interactive controls).
    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    // TAGS | ANALYSIS. The switch lives in the CHROME here, because in this window the chrome IS the
    // pane header - the panel stopped drawing its own tab bar so that a docked host (JUST TAG) does
    // not end up with two rows of tabs.
    private void OnShowTags(object? sender, RoutedEventArgs e) => Panel.ShowAnalysis = false;

    private void OnShowAnalysis(object? sender, RoutedEventArgs e) => Panel.ShowAnalysis = true;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Closing with unsaved edits asks the same three-way question as switching files. The close is
    /// cancelled first and re-issued after the answer, because the dialog is async and a
    /// <see cref="Window.Closing"/> handler cannot wait.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose && Editor.IsDirty)
        {
            e.Cancel = true;
            _ = ConfirmThenCloseAsync();
            return;
        }
        base.OnClosing(e);
    }

    private async Task ConfirmThenCloseAsync()
    {
        if (Body is { } body && !await body.ConfirmLeaveAsync(this)) return;
        _forceClose = true;
        Close();
    }
}
