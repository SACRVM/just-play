using System;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using JustPlay.App.ViewModels;
using JustPlay.Core.Models;

namespace JustPlay.App.Controls;

/// <summary>
/// Slide-in tweaks panel (theme palette · stage toggles · audio), shared as a self-contained
/// control. Binds to the inherited <c>MainWindowViewModel</c> DataContext; the consumer controls
/// visibility and docking. (About moved to the title-bar brand; compact layout has its own button.)
/// </summary>
public partial class TweaksPanel : UserControl
{
    public TweaksPanel() => InitializeComponent();

    // ── Saved-preset right-click menu (Replace / Rename / Delete) ───────────────────
    // Done in code-behind, NOT via Command bindings: the ContextMenu is a popup outside this
    // control's visual tree, so RelativeSource/PlacementTarget command bindings resolve null and
    // the items render permanently disabled. The handlers resolve the clicked preset + the VM
    // directly. (Mirrors MaxView's OnStreamMenuOpening pattern.)

    private void OnPresetReplace(object? sender, RoutedEventArgs e) => RunPresetAction(sender, vm => vm.ReplacePresetCommand);
    private void OnPresetRename(object? sender, RoutedEventArgs e)  => RunPresetAction(sender, vm => vm.RenamePresetCommand);
    private void OnPresetDelete(object? sender, RoutedEventArgs e)  => RunPresetAction(sender, vm => vm.DeletePresetCommand);

    private void RunPresetAction(object? sender, Func<MainWindowViewModel, ICommand> pick)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not MenuItem mi)
            return;

        // The chip's DataContext (the DspPreset) is inherited by the ContextMenu/MenuItem; fall back
        // to the ContextMenu's PlacementTarget (the chip) in case popup inheritance didn't propagate.
        var preset = mi.DataContext as DspPreset
                     ?? (mi.FindLogicalAncestorOfType<ContextMenu>()?.PlacementTarget as Control)?.DataContext as DspPreset;
        if (preset is null)
            return;

        pick(vm).Execute(preset);
    }
}
