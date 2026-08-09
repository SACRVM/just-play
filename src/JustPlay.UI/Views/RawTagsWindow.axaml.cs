using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using JustPlay.Core.Abstractions;
using JustPlay.UI.Behaviors;
using JustPlay.UI.Controls;
using JustPlay.UI.ViewModels;

namespace JustPlay.UI.Views;

/// <summary>
/// RAW TAGS - the SHARED window that shows a file's tag containers exactly as they sit on disk.
/// See <see cref="RawTagsViewModel"/> for what it is for, and the XAML header for why the shell and
/// the caution colours are copied rather than invented.
///
/// <para><b>Strictly read-only.</b> There is no edit, no delete and no "strip foreign tags" here,
/// not now and not as a stub. The only thing that leaves this window is text: a click on a row copies
/// that line, and the footer button copies the whole listing for the open file.</para>
///
/// <para>Takes <see cref="IRawTagReader"/> as a parameter rather than resolving one. The abstraction
/// lives in JustPlay.Core, which the shared UI library already references; the implementation lives in
/// JustPlay.Metadata, which it must never reference (CLAUDE.md, layering).</para>
/// </summary>
public partial class RawTagsWindow : Window
{
    public RawTagsWindow()
    {
        InitializeComponent();

        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!,
                                      this.FindControl<Border>("DialogCard"));

        // One key for the whole suite, not one per app: this is the same window wherever it is opened
        // from, and where you like to put it is a habit of yours, not of JUST TAG's.
        WindowPlacement.Track(this, "Just.RawTags");
    }

    /// <summary>
    /// Show the raw tags for <paramref name="paths"/> and wait until the window is closed. The first
    /// file is read immediately; the rest only when they are stepped to.
    /// </summary>
    /// <param name="reader">The raw tag reader - <c>TagLibRawTagReader</c> in every host today.</param>
    /// <param name="paths">The selection, in the order the caller wants them stepped through. An
    /// empty selection opens nothing.</param>
    /// <param name="convertsToLabel">
    /// The ID3v2 container label a save would convert these files TO -
    /// <c>Id3VersionProbe.Name(major)</c> for <c>Id3VersionProbe.MajorFor(settings.WriteFormat)</c> -
    /// or null (the default) when the configured write format keeps each file's existing version.
    /// Null means no conversion can happen, so the version notice can never appear. Only JUST TAG has
    /// a write format to pass; JUST PLAY, the finder and the CLI never convert.
    /// </param>
    public static Task ShowAsync(Window owner, IRawTagReader reader, IReadOnlyList<string> paths,
                                 string? convertsToLabel = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0) return Task.CompletedTask;

        var window = new RawTagsWindow
        {
            DataContext = new RawTagsViewModel(reader, paths, convertsToLabel),
        };

        return window.ShowDialog(owner);
    }

    private RawTagsViewModel? Model => DataContext as RawTagsViewModel;

    private void OnPrev(object? sender, RoutedEventArgs e)
    {
        Model?.Prev();
        CopiedHint.IsVisible = false;
    }

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        Model?.Next();
        CopiedHint.IsVisible = false;
    }

    /// <summary>A container's header is the whole-width toggle for it - the section object carries its
    /// own expanded state, so a fold survives stepping to another file and back.</summary>
    private void OnToggleSection(object? sender, RoutedEventArgs e) =>
        ((sender as Button)?.DataContext as RawTagSection)?.Toggle();

    private async void OnCopyRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is RawTagRow row)
            await CopyAsync(row.Line.TrimEnd());
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e) =>
        await CopyAsync(Model?.ListingText);

    // Through the SHARED clipboard helper, which exists because of the crash this window found: the
    // Win32 clipboard raises from inside the copy call, and an exception out of an `async void`
    // handler terminates the process. Every row here is a copy button, so that path is clicked
    // constantly. A copy that did not happen is worth one word, never the app.
    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var copied = await SystemClipboard.CopyTextAsync(this, text);

        CopiedHint.Text = copied ? "Copied" : "Could not copy";
        CopiedHint.Classes.Set("failed", !copied);
        CopiedHint.IsVisible = true;
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Escape closes; Left/Right step through the selection. The arrows are free here - the list
    /// below scrolls vertically, so nothing else wants them - and stepping is the one thing this
    /// window does repeatedly, which is exactly what earns a key.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                OnPrev(this, e);
                e.Handled = true;
                break;
            case Key.Right:
                OnNext(this, e);
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }
}
