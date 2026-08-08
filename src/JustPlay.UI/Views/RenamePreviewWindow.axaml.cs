using System.Collections.Generic;
using System.Linq;
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
/// Every old -> new name a rename pattern would produce, shown before anything moves.
///
/// <para>Read-only on purpose: it is a look, not a step. You either accept what you see and save, or
/// you go back and pick a different pattern. Making it editable would turn one decision into 37.</para>
/// </summary>
public partial class RenamePreviewWindow : Window
{
    public RenamePreviewWindow()
    {
        InitializeComponent();
        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!,
                                      this.FindControl<Border>("DialogCard"));
    }

    /// <summary>
    /// Show the preview for <paramref name="rows"/> and wait until it is closed.
    /// </summary>
    /// <param name="title">The chrome label - this window serves BOTH directions of the mask
    /// language, and which one you are looking at is the first thing it has to say.</param>
    /// <param name="left">Header over the left column ("NOW" / "FILE").</param>
    /// <param name="right">Header over the right column ("AFTER" / "WOULD GET").</param>
    public static Task ShowAsync(Window owner, string title, string left, string right,
                                 string mask, IReadOnlyList<RenamePreviewRow> rows)
    {
        var window = new RenamePreviewWindow();

        window.FindControl<TextBlock>("TitleText")!.Text = title;
        window.FindControl<TextBlock>("LeftHeader")!.Text = left;
        window.FindControl<TextBlock>("RightHeader")!.Text = right;
        window.FindControl<TextBlock>("MaskText")!.Text = mask;
        window.FindControl<ItemsControl>("Rows")!.ItemsSource = rows;

        window.FindControl<TextBlock>("SummaryText")!.Text = Summarise(rows);

        return window.ShowDialog(owner);
    }

    /// <summary>
    /// The one sentence that decides whether you go back to the pattern or go ahead.
    /// <para>The problem count leads when there is one - "35 of 37" buries the fact that two of them
    /// would collide, and a count you have to subtract in your head is a count you will get wrong.</para>
    /// </summary>
    private static string Summarise(IReadOnlyList<RenamePreviewRow> rows)
    {
        var files = rows.Count == 1 ? "1 file" : $"{rows.Count} files";

        // A name that does not FIT the pattern is not a problem to be fixed - that file is simply
        // left alone, which is the safe answer and the honest one. A COLLISION is a problem: two
        // files cannot have one name, and the save refuses until it is gone.
        var skipped = rows.Count(r => r.Issue == RenameIssue.NoMatch);
        var blocking = rows.Count(r => r.IsProblem && r.Issue != RenameIssue.NoMatch);

        if (blocking > 0)
            return $"{files}, but {blocking} cannot go ahead - fix them or pick another pattern. "
                   + "Saving is blocked until they are clear.";

        if (skipped == rows.Count)
            return $"None of these {rows.Count} names fit this pattern - nothing would change.";

        if (skipped > 0)
            return $"{rows.Count - skipped} of {files} would change; "
                   + $"{skipped} do not fit the pattern and stay as they are. "
                   + "Nothing is written until you save.";

        return $"All {files} would change. Nothing is written until you save.";
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
