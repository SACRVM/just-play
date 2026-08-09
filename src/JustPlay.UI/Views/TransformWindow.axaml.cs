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
/// TRANSFORM - change the text that is already in the tags, across a whole selection. See the XAML
/// header for what it is for and why the preview is the safety model rather than a courtesy.
///
/// <para>The window itself holds no rules: which fields, which operation and what would change all
/// live in <see cref="TagTransformViewModel"/>, so the same transform could be driven from a second
/// host - or from a test - without this file.</para>
/// </summary>
public partial class TransformWindow : Window
{
    private bool _wrote;

    public TransformWindow()
    {
        InitializeComponent();
        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!,
                                       this.FindControl<Border>("DialogCard"));
        WindowPlacement.Track(this, "Just.Transform");
    }

    /// <summary>
    /// Show the transform for <paramref name="vm"/>'s files and wait until it is closed. Returns true
    /// when something was actually written, which is the host's cue to re-read its rows.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, TagTransformViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        var window = new TransformWindow { DataContext = vm };
        window.FindControl<TextBlock>("CountText")!.Text =
            vm.FileCount == 1 ? "1 file" : $"{vm.FileCount} files";

        await window.ShowDialog(owner);
        return window._wrote;
    }

    /// <summary>
    /// Rows a host hands over before their tags have been read cannot be previewed until they are -
    /// and "not read yet" must never quietly read as "nothing changes here". Started once the window
    /// is up, so the overlay has something to appear over.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is TagTransformViewModel vm) _ = PrimeAsync(vm);
    }

    private static async Task PrimeAsync(TagTransformViewModel vm)
    {
        try { await vm.PrimeAsync(); }
        catch (Exception)
        {
            // A file that will not open simply has nothing to preview; the window stays usable for
            // the rest, and a fire-and-forget task must never let anything escape.
        }
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not TagTransformViewModel vm || !vm.CanApply) return;

            var report = await vm.ApplyAsync();
            _wrote = report.Written > 0 || report.Deferred > 0;

            // A clean run is done, and what you want back is the list behind this window. Anything
            // that FAILED keeps it open instead: the reason belongs on screen, and the preview has to
            // be rebuilt from what the files say NOW - otherwise it would offer to make changes that
            // have already happened.
            if (report.Failed == 0) { Close(); return; }

            await vm.RefreshAsync();
            vm.Report(report);
        }
        catch (Exception)
        {
            // `async void` on an event handler must not let anything escape.
        }
    }

    /// <summary>Escape is the way out of a dialog everywhere else in the suite, so it is here too.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
