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
    public static Task ShowAsync(Window owner, string mask, IReadOnlyList<RenamePreviewRow> rows)
    {
        var window = new RenamePreviewWindow();

        window.FindControl<TextBlock>("MaskText")!.Text = mask;
        window.FindControl<ItemsControl>("Rows")!.ItemsSource = rows;

        var problems = rows.Count(r => r.IsProblem);
        window.FindControl<TextBlock>("SummaryText")!.Text = Summarise(rows.Count, problems);

        return window.ShowDialog(owner);
    }

    /// <summary>
    /// The one sentence that decides whether you go back to the pattern or go ahead.
    /// <para>The problem count leads when there is one - "35 of 37" buries the fact that two of them
    /// would collide, and a count you have to subtract in your head is a count you will get wrong.</para>
    /// </summary>
    private static string Summarise(int total, int problems)
    {
        var files = total == 1 ? "1 file" : $"{total} files";

        return problems switch
        {
            0 => $"{files} would be renamed. Nothing is renamed until you save.",
            1 => $"{files}, but 1 cannot be renamed - fix it or pick another pattern. "
                 + "Saving is blocked until it is clear.",
            _ => $"{files}, but {problems} cannot be renamed - fix them or pick another pattern. "
                 + "Saving is blocked until they are clear.",
        };
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (WindowChrome.IsInteractive(e.Source as Visual)) return;
        BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
