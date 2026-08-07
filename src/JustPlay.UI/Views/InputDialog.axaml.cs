using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JustPlay.UI.Views;

/// <summary>
/// Shared tiny themed single-line text-input dialog for the J.U.S.T. suite. <see cref="AskAsync"/>
/// shows it modally over an owner window and returns the trimmed entry, or null if the user cancels /
/// leaves it empty. Enter confirms, Esc cancels. (Lifted from JUST PLAY into JustPlay.UI so both apps
/// share one dialog - used e.g. for naming a Sound preset.)
/// </summary>
public partial class InputDialog : Window
{
    public InputDialog() => InitializeComponent();

    /// <summary>Prompt for a string. Returns the trimmed value, or null on cancel / empty input.</summary>
    public static async Task<string?> AskAsync(Window owner, string prompt, string initial)
    {
        var dlg = new InputDialog();
        dlg.PromptText.Text = prompt;
        dlg.Input.Text = initial;
        dlg.Input.SelectAll();
        dlg.Input.Focus();
        return await dlg.ShowDialog<string?>(owner);
    }

    private string? Result() => string.IsNullOrWhiteSpace(Input.Text) ? null : Input.Text!.Trim();

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Result());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Close(Result());
        else if (e.Key == Key.Escape) Close(null);
        base.OnKeyDown(e);
    }
}
