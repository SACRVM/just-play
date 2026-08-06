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
/// The shared tag-editor body — see <see cref="TagEditorPanel"/>'s XAML header. Code-behind holds
/// only what needs a <see cref="TopLevel"/> (the cover picker) and the three button clicks; every
/// decision lives in <see cref="TagEditorViewModel"/>, so JUST TAG's sidebar gets identical
/// behaviour by construction rather than by care.
/// </summary>
public partial class TagEditorPanel : UserControl
{
    public TagEditorPanel() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Which half is showing: the editable TAGS, or the read-only ANALYSIS. It lives on the CONTROL,
    /// not the view model — the view model is the file's state and is shared with whatever else a host
    /// wires to it, while "which half am I looking at" belongs to this panel.
    ///
    /// <para>The panel no longer draws the switch itself (Chloe 2026-08-05): the HOST owns its pane
    /// header, and a panel with its own tab bar produced a second row of tabs right under the first.
    /// Hosts bind this — JUST TAG from its EDITOR | ANALYSIS | FILTER header, the floating window from
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

    private void OnSave(object? sender, RoutedEventArgs e) => Vm?.Save();

    private void OnRevert(object? sender, RoutedEventArgs e) => Vm?.Revert();

    private void OnRemoveCover(object? sender, RoutedEventArgs e) => Vm?.RemoveCover();

    /// <summary>A quick-rename pattern was picked. It only FILLS the name box — the rename happens
    /// on Save, so the result is visible and editable first.</summary>
    private void OnRenameMask(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string mask }) Vm?.ApplyRenameMask(mask);
    }

    /// <summary>Put the file's real name back in the box — the exit from a rename that does not
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
            // Unreadable image / cancelled picker — leave the cover as it was. `async void` on an
            // event handler cannot let an exception escape, so it is caught here on purpose.
        }
    }

    /// <summary>
    /// Ask about unsaved edits before the target changes. Returns false only when the user picked
    /// Cancel, i.e. "stay on this file" — a mis-click must never be able to throw the work away,
    /// which is why this is a THREE-way question and not a yes/no.
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync(Window owner)
    {
        if (Vm is not { IsDirty: true } vm) return true;

        var choice = await ConfirmDialog.AskSaveDiscardCancelAsync(
            owner,
            "Unsaved changes",
            $"\"{vm.FileName}\" has edits you haven't saved.");

        return choice switch
        {
            SaveChoice.Save    => vm.Save(),   // a refused save (bad input) keeps us on this file
            SaveChoice.Discard => true,
            _                  => false,
        };
    }
}
