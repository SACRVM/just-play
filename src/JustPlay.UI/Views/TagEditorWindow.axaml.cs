using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.ViewModels;

namespace JustPlay.UI.Views;

/// <summary>
/// The floating, always-on-top tag editor — shared by JUST PLAY and the PRE CUE FINDER, and the
/// same <see cref="TagEditorPanel"/> body JUST TAG will dock as its sidebar.
///
/// <para><b>It follows the SELECTION, not playback.</b> The host calls
/// <see cref="SetTargetAsync"/> when the user picks a different row; the track ending and the next
/// one loading must NOT call it. Retargeting on playback would pop a save prompt in the middle of
/// typing, triggered by an event the user did not cause.</para>
///
/// <para>When there are unsaved edits and the user answers <see cref="SaveChoice.Cancel"/>, the
/// editor KEEPS its current file — the list selection has already moved, and that is fine: the file
/// being edited is named at the top of the panel, so the two being temporarily out of step is
/// visible rather than silent. Yanking the list's selection back would be worse.</para>
/// </summary>
public partial class TagEditorWindow : Window
{
    private bool _forceClose;

    /// <summary>Parameterless ctor for the XAML previewer.</summary>
    public TagEditorWindow() : this(null) { }

    public TagEditorWindow(TagEditorViewModel? editor)
    {
        InitializeComponent();

        // TransparencyLevelHint comes from the XAML ONLY — re-setting it here trips Avalonia's
        // macOS opaque fallback (black surround). Measured 2026-07-31: Win32 fixes a window's
        // transparency at CREATION, so a later assignment does nothing anyway.

        Editor = editor ?? throw new ArgumentNullException(nameof(editor),
            "The tag editor needs its view model; the parameterless ctor exists for the previewer only.");

        DataContext = Editor;
        WindowPlacement.Track(this, "Just.TagEditor");
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
        if (string.Equals(Editor.FilePath, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (Body is { } body && !await body.ConfirmLeaveAsync(this)) return false;

        if (path is null) Editor.Clear();
        else Editor.Load(path);
        return true;
    }

    /// <summary>
    /// Open the editor, or bring the existing one forward — a second instance would give one file
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
