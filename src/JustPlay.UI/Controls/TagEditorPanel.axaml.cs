using System;
using System.ComponentModel;
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
    /// Which body is showing: the editable TAGS, the read-only ANALYSIS, or the read-only RAW frames.
    /// It lives on the CONTROL, not the view model - the view model is the file's state and is shared
    /// with whatever else a host wires to it, while "which one am I looking at" belongs to this panel.
    ///
    /// <para>The panel no longer draws the switch itself: the HOST owns its pane
    /// header, and a panel with its own tab bar produced a second row of tabs right under the first.
    /// Hosts bind this - JUST TAG from its EDITOR | ANALYSIS | FILTER header, the floating window from
    /// its chrome.</para>
    ///
    /// <para>Two flags rather than one enum because a host may only know about the one it drives:
    /// they are kept mutually exclusive here (see <see cref="OnTabFlagChanged"/>), and TAGS is what
    /// is left when neither is on.</para>
    /// </summary>
    public static readonly StyledProperty<bool> ShowAnalysisProperty =
        AvaloniaProperty.Register<TagEditorPanel, bool>(nameof(ShowAnalysis));

    public bool ShowAnalysis
    {
        get => GetValue(ShowAnalysisProperty);
        set => SetValue(ShowAnalysisProperty, value);
    }

    /// <summary>The RAW frames of the open file. Same mechanism as <see cref="ShowAnalysis"/> - the
    /// host's header drives it, this panel switches its body.</summary>
    public static readonly StyledProperty<bool> ShowRawProperty =
        AvaloniaProperty.Register<TagEditorPanel, bool>(nameof(ShowRaw));

    public bool ShowRaw
    {
        get => GetValue(ShowRawProperty);
        set => SetValue(ShowRawProperty, value);
    }

    /// <summary>The editable body - what is showing when neither of the two read-only bodies is.
    /// Read-only and derived, so no host has to keep a third flag in step.</summary>
    public static readonly DirectProperty<TagEditorPanel, bool> ShowTagsProperty =
        AvaloniaProperty.RegisterDirect<TagEditorPanel, bool>(nameof(ShowTags), o => o.ShowTags);

    private bool _showTags = true;

    public bool ShowTags
    {
        get => _showTags;
        private set => SetAndRaise(ShowTagsProperty, ref _showTags, value);
    }

    static TagEditorPanel()
    {
        ShowAnalysisProperty.Changed.AddClassHandler<TagEditorPanel>((p, e) => p.OnTabFlagChanged(e));
        ShowRawProperty.Changed.AddClassHandler<TagEditorPanel>((p, e) => p.OnTabFlagChanged(e));
    }

    /// <summary>
    /// The two flags are ONE choice, so whichever was just switched on switches the other off. A host
    /// that drives only the flag it knows about therefore cannot leave the panel with two bodies up.
    ///
    /// <para><see cref="AvaloniaObject.SetCurrentValue{T}"/>, never <c>SetValue</c>: JUST TAG BINDS
    /// <see cref="ShowAnalysis"/> to its pane state, and a local value would win over that binding
    /// permanently - the tab would work once and then be stuck.</para>
    /// </summary>
    private void OnTabFlagChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            if (e.Property == ShowAnalysisProperty) SetCurrentValue(ShowRawProperty, false);
            else SetCurrentValue(ShowAnalysisProperty, false);
        }

        ShowTags = !ShowAnalysis && !ShowRaw;

        // Reading a file's raw containers is a second open of it, so it does not happen until the tab
        // is actually looked at. From here on the view model keeps it in step by itself.
        if (ShowRaw) Vm?.EnsureRaw();
    }

    private TagEditorViewModel? Vm => DataContext as TagEditorViewModel;

    private TagEditorViewModel? _watched;

    /// <summary>
    /// A body that stops being available has to leave you somewhere, and that rule lives HERE rather
    /// than in each host: both read-only bodies are single-file views, so picking a selection takes
    /// them away, and a host that forgot to reset would be left rendering one over a file it is not
    /// pointed at.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnEditorPropertyChanged;
        _watched = Vm;
        if (_watched is not null) _watched.PropertyChanged += OnEditorPropertyChanged;

        SyncAvailableTabs();
        base.OnDataContextChanged(e);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TagEditorViewModel.CanShowAnalysis)
                           or nameof(TagEditorViewModel.CanShowRaw))
            SyncAvailableTabs();
    }

    /// <summary>Fall back to TAGS when the body that is up has just gone away.
    /// <see cref="AvaloniaObject.SetCurrentValue{T}"/> so a host's binding survives - see
    /// <see cref="OnTabFlagChanged"/>.</summary>
    private void SyncAvailableTabs()
    {
        if (Vm is not { } vm) return;
        if (ShowAnalysis && !vm.CanShowAnalysis) SetCurrentValue(ShowAnalysisProperty, false);
        if (ShowRaw && !vm.CanShowRaw) SetCurrentValue(ShowRawProperty, false);

        // Covers the host that has the panel on RAW BEFORE it hands over the view model - the flag
        // changed while there was nothing to ask, so the ask happens here instead. Idempotent.
        if (ShowRaw) vm.EnsureRaw();
    }

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

    // ================================================================================================
    // RAW - the file's containers as they sit on disk. Read-only by contract: nothing in here edits,
    // deletes or strips anything, and the ONLY thing that leaves it is text.
    // ================================================================================================

    /// <summary>A container's header is the whole-width toggle for it. The section object carries its
    /// own expanded state, so a fold survives a switch to another tab and back.</summary>
    private void OnToggleRawSection(object? sender, RoutedEventArgs e) =>
        ((sender as Button)?.DataContext as RawTagSection)?.Toggle();

    private async void OnCopyRawRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is RawTagRow row)
            await CopyRawAsync(row.Line.TrimEnd());
    }

    private async void OnCopyRawAll(object? sender, RoutedEventArgs e) =>
        await CopyRawAsync(Vm?.Raw?.ListingText);

    /// <summary>
    /// Through the SHARED clipboard helper, which exists because of the crash this view found: the
    /// Win32 clipboard raises from inside the copy call, and an exception out of an <c>async void</c>
    /// handler terminates the process. Every row here is a copy button, so that path gets clicked
    /// constantly. A copy that did not happen is worth one word, never the app.
    /// </summary>
    private async Task CopyRawAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var copied = await SystemClipboard.CopyTextAsync(TopLevel.GetTopLevel(this), text);

        // The word is the feedback, not the copy: a missing hint must never be able to stop the copy
        // itself, so it is looked up after the work and not before it.
        if (this.FindControl<TextBlock>("RawCopiedHint") is not { } hint) return;

        hint.Text = copied ? "Copied" : "Could not copy";
        hint.Classes.Set("failed", !copied);
        hint.IsVisible = true;
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
