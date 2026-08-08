using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using JustPlay.UI.ViewModels;

namespace JustPlay.UI.Views;

/// <summary>
/// The confirmation in front of a multi-file save - see the XAML header.
///
/// <para>It is deliberately NOT a yes/no about a count. "Write to 37 files?" is a question nobody can
/// answer, because the thing you would be agreeing to is the list of fields and values, and that list
/// is spread across a panel you may have scrolled away from.</para>
/// </summary>
public partial class TagSaveConfirmWindow : Window
{
    private bool _confirmed;

    public TagSaveConfirmWindow() => InitializeComponent();

    /// <summary>
    /// Show the plan and wait. Returns true when the user chose to write.
    /// </summary>
    public static async Task<bool> AskAsync(Window owner, TagSavePlan plan)
    {
        var window = new TagSaveConfirmWindow();

        var files = plan.FileCount == 1 ? "1 file" : $"{plan.FileCount} files";
        window.FindControl<TextBlock>("TitleText")!.Text = $"Write to {files}";
        window.FindControl<Button>("WriteButton")!.Content = $"Write {files}";
        window.FindControl<ItemsControl>("Rows")!.ItemsSource = plan.Sets;

        if (plan.Cover is CoverPlan.Replace or CoverPlan.Remove)
        {
            var cover = window.FindControl<TextBlock>("CoverText")!;
            cover.Text = plan.Cover == CoverPlan.Remove
                ? "The cover is removed from every file."
                : "The chosen cover is written to every file.";
            cover.IsVisible = true;
        }

        if (plan.TagFromNameMask is { Length: > 0 } fromName)
        {
            var fields = plan.TagFromNameFields is { Count: > 0 } f
                ? string.Join(", ", f.Select(x => x.ToUpperInvariant()))
                : "";
            var line = window.FindControl<TextBlock>("FromNameText")!;
            // Named separately from the value list above, because these do NOT get one value: each
            // file gets what its own name says. A file whose name does not fit is left alone.
            line.Text = $"{fields} come from each file's own name  ({fromName})";
            line.IsVisible = true;
        }

        if (plan.RenameMask is { Length: > 0 } mask)
        {
            var rename = window.FindControl<TextBlock>("RenameText")!;
            // The pattern verbatim, not a friendly name: it is what actually runs, and the eye in the
            // panel is where you go to see what it produces.
            rename.Text = $"Files are renamed after the pattern  {mask}";
            rename.IsVisible = true;
        }

        await window.ShowDialog(owner);
        return window._confirmed;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
