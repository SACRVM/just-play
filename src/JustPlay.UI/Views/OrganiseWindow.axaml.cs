using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.ViewModels;

namespace JustPlay.UI.Views;

/// <summary>
/// MOVE / COPY / DELETE for a selection of tracks - see the XAML header for what the window is for
/// and why the preview is the safety model rather than a courtesy.
///
/// <para>The window holds no rules. Which files, where they land, what collides and what it costs
/// are all <see cref="OrganiseViewModel"/>'s, which in turn owns nothing either - it asks
/// <c>OrganisePlanner</c> and hands the answer to <c>OrganiseRunner</c>. The same operation could be
/// driven from a second host, or from a test, without this file.</para>
/// </summary>
public partial class OrganiseWindow : Window
{
    private bool _changed;

    public OrganiseWindow()
    {
        InitializeComponent();
        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!,
                                       this.FindControl<Border>("DialogCard"));
        WindowPlacement.Track(this, "Just.Organise");

        // Re-plan every time the window comes back to the front. Files move underneath a window that
        // has been sitting open - the other app, the other machine, her own last operation - and a
        // preview that is quietly out of date is worse than no preview.
        Activated += (_, _) => { if (Vm is { IsBusy: false } vm) vm.Refresh(); };
    }

    private OrganiseViewModel? Vm => DataContext as OrganiseViewModel;

    /// <summary>
    /// Show the preview and wait until it is closed. Returns true when something actually happened
    /// on disk, which is the host's cue to re-read its folder.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, OrganiseViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        var window = new OrganiseWindow { DataContext = vm };
        await window.ShowDialog(owner);
        return window._changed;
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { IsBusy: false } vm) return;

            // Start where she already is - or at the nearest ancestor that still exists, because a
            // NAS path that is offline would otherwise dump the picker somewhere random.
            var start = vm.PickerStart;
            while (!string.IsNullOrEmpty(start) && !Directory.Exists(start))
                start = Path.GetDirectoryName(start);

            var options = new FolderPickerOpenOptions
            {
                Title = vm.Action == JustPlay.Core.Organise.OrganiseAction.Move
                    ? "Move these files into..."
                    : "Copy these files into...",
                AllowMultiple = false,
            };
            if (!string.IsNullOrEmpty(start))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);

            var picked = await StorageProvider.OpenFolderPickerAsync(options);
            if (picked?.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
                vm.Destination = path;
        }
        catch (Exception)
        {
            // Cancelled, or a provider that refused - leave the destination where it was. `async
            // void` on an event handler must not let anything escape.
        }
    }

    /// <summary>One of the remembered destinations. The path rides on the button's Tag so the
    /// handler needs no lookup back into the list.</summary>
    private void OnRecent(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && Vm is { IsBusy: false } vm)
            vm.Destination = path;
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { CanRun: true } vm) return;

            var report = await vm.RunAsync();
            if (report.Succeeded > 0) _changed = true;

            // A clean run is done, and what you want back is the list behind this window. Anything
            // that FAILED keeps it open instead: the reason belongs on screen, and the preview is
            // rebuilt from what the disk says NOW so it cannot offer to redo what already happened.
            if (report.Failed == 0 && !report.Cancelled) { Close(); return; }

            vm.Refresh();
        }
        catch (Exception)
        {
            // `async void` on an event handler must not let anything escape - and an operation that
            // could not start is not worth taking the tagger down for.
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e) => Vm?.Cancel();

    /// <summary>
    /// While a run is in flight the only way out is to STOP it. Closing the window would leave the
    /// copy going with nobody to report to - and nobody to tell the library index what moved.
    /// </summary>
    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (Vm is { IsBusy: true } vm) { vm.Cancel(); return; }
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Vm is { IsBusy: true } vm)
        {
            vm.Cancel();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Escape is the way out of a dialog everywhere else in the suite, so it is here too -
    /// and while a run is going it is the way to stop it.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (Vm is { IsBusy: true } vm) vm.Cancel(); else Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }
}
