using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using JustPlay.UI.ViewModels;
using JustPlay.UI.Views;

namespace JustPlay.UI.Controls;

/// <summary>
/// The shared tag-editor body - see <see cref="TagEditorPanel"/>'s XAML header. Code-behind holds
/// only what needs a <see cref="TopLevel"/> (the cover picker) and the three button clicks; every
/// decision lives in <see cref="TagEditorViewModel"/>, so JUST TAG's sidebar gets identical
/// behaviour by construction rather than by care.
/// </summary>
public partial class TagEditorPanel : UserControl
{
    public TagEditorPanel()
    {
        AvaloniaXamlLoader.Load(this);
        if (this.FindControl<AutoCompleteBox>("TagMask") is { } mask) BareInput.Apply(mask);
    }

    /// <summary>
    /// Which half is showing: the editable TAGS, or the read-only ANALYSIS. It lives on the CONTROL,
    /// not the view model - the view model is the file's state and is shared with whatever else a host
    /// wires to it, while "which half am I looking at" belongs to this panel.
    ///
    /// <para>The panel no longer draws the switch itself (Chloe 2026-08-05): the HOST owns its pane
    /// header, and a panel with its own tab bar produced a second row of tabs right under the first.
    /// Hosts bind this - JUST TAG from its EDITOR | ANALYSIS | FILTER header, the floating window from
    /// its chrome.</para>
    /// </summary>
    public static readonly StyledProperty<bool> ShowAnalysisProperty =
        AvaloniaProperty.Register<TagEditorPanel, bool>(nameof(ShowAnalysis));

    public bool ShowAnalysis
    {
        get => GetValue(ShowAnalysisProperty);
        set => SetValue(ShowAnalysisProperty, value);
    }

    private TagEditorViewModel? Vm => DataContext as TagEditorViewModel;

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        try { await SaveAsync(); }
        catch (Exception)
        {
            // `async void` on an event handler cannot let anything escape. Whatever went wrong has
            // already landed in the view model's Status line, which is where the user reads it.
        }
    }

    /// <summary>
    /// One file saves straight away; a selection asks first.
    /// <para>The asking is not ceremony: writing tags into dozens of files cannot be taken back, and
    /// what is about to happen is spread over eleven ticks the user may have scrolled past. The
    /// dialog is handed the SAME plan object the save then executes, so it cannot promise one thing
    /// and do another.</para>
    /// </summary>
    private async Task SaveAsync()
    {
        if (Vm is not { } vm) return;

        if (!vm.IsMulti) { vm.Save(); return; }

        var plan = vm.BuildPlan();
        if (!plan.HasWork) { await vm.SaveManyAsync(); return; }   // sets "Nothing to save."

        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await TagSaveConfirmWindow.AskAsync(owner, plan)) return;

        await vm.SaveManyAsync();
    }

    private void OnRevert(object? sender, RoutedEventArgs e) => Vm?.Revert();

    private void OnRemoveCover(object? sender, RoutedEventArgs e) => Vm?.RemoveCover();

    /// <summary>A quick-rename pattern was picked. It only FILLS the name box - the rename happens
    /// on Save, so the result is visible and editable first.</summary>
    private void OnRenameMask(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string mask }) Vm?.ApplyRenameMask(mask);
    }

    /// <summary>
    /// Across a selection the same patterns are a CHOICE rather than a fill: there is no one box to
    /// put a name in, so the pattern is kept and resolved per file at save time. A menu item with no
    /// Tag is "leave names alone", which clears it.
    /// </summary>
    private void OnPickMask(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.RenameMask = (sender as Control)?.Tag as string;
    }

    /// <summary>
    /// The eye: what every selected file would end up called. Opened BEFORE the save, because the two
    /// ways a bulk rename goes wrong - two files landing on one name, a name the folder already holds
    /// - are invisible in the pattern and obvious in the list.
    /// </summary>
    private async void OnPreviewNames(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { RenameMask: { Length: > 0 } mask } vm) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            await RenamePreviewWindow.ShowAsync(owner, "RENAME PREVIEW", "NOW", "AFTER",
                                                mask, vm.PreviewRenames());
        }
        catch (Exception)
        {
            // `async void` on an event handler must not let anything escape. A preview that fails to
            // open costs nothing - no file has been touched at this point, by construction.
        }
    }

    /// <summary>
    /// The pattern that reads TAGS out of a name. One file: it fills the boxes above, so the result
    /// is on screen and correctable. A selection: it is kept as a plan and applied per file, and the
    /// fields it owns go read-only - a box cannot show a value that differs in every file.
    /// </summary>
    private void OnPickTagMask(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.TagFromNameMask = (sender as Control)?.Tag as string;
    }

    /// <summary>The pattern cheat sheet - one window, parked wherever you put it.</summary>
    private void OnMaskHelp(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner) MaskHelpWindow.Open(owner);
    }

    /// <summary>The eye for the other direction: what each file's name would give it. A name that
    /// does not fit the pattern is shown as left alone, which is the answer and not an error.</summary>
    private async void OnPreviewTagsFromName(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { TagFromNameMask: { Length: > 0 } mask } vm) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            await RenamePreviewWindow.ShowAsync(owner, "TAGS FROM NAME", "FILE", "WOULD GET",
                                                mask, vm.PreviewTagsFromName());
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Put the file's real name back in the box - the exit from a rename that does not
    /// throw the tag edits away with it.</summary>
    private void OnRenameOriginal(object? sender, RoutedEventArgs e) => Vm?.RestoreOriginalName();

    private async void OnChangeCover(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { } vm) return;
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose cover image",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Image") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"] },
                    FilePickerFileTypes.All,
                ],
            });

            var path = files?.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            var bytes = await File.ReadAllBytesAsync(path);
            var mime = Path.GetExtension(path).ToLowerInvariant() is ".png" ? "image/png" : "image/jpeg";
            vm.SetNewCover(bytes, mime);
        }
        catch (Exception)
        {
            // Unreadable image / cancelled picker - leave the cover as it was. `async void` on an
            // event handler cannot let an exception escape, so it is caught here on purpose.
        }
    }

    /// <summary>
    /// Ask about unsaved edits before the target changes. Returns false only when the user picked
    /// Cancel, i.e. "stay on this file" - a mis-click must never be able to throw the work away,
    /// which is why this is a THREE-way question and not a yes/no.
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync(Window owner)
    {
        if (Vm is not { IsDirty: true } vm) return true;

        var choice = await ConfirmDialog.AskSaveDiscardCancelAsync(
            owner,
            "Unsaved changes",
            vm.IsMulti
                ? $"{vm.FileCount} files have edits you haven't saved."
                : $"\"{vm.FileName}\" has edits you haven't saved.");

        if (choice == SaveChoice.Discard) return true;
        if (choice != SaveChoice.Save) return false;

        // A refused save (bad input, an unresolved name clash) keeps us where we are - the edits are
        // still there, and moving on would throw them away behind the user's back.
        if (!vm.IsMulti) return vm.Save();

        await SaveAsync();
        return !vm.IsDirty;
    }
}
