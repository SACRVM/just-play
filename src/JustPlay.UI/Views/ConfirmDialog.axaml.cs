using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JustPlay.UI.Views;

/// <summary>
/// Shared tiny themed yes/no confirmation dialog for the J.U.S.T. suite — the counterpart
/// to <see cref="InputDialog"/> for actions that would discard work ("New project?",
/// destructive resets). <see cref="AskAsync"/> shows it modally and returns true only on
/// an explicit confirm; Esc / Cancel / closing = false. Enter confirms, Esc cancels.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    /// <summary>Ask a yes/no question. Returns true ONLY on explicit confirm.</summary>
    public static async Task<bool> AskAsync(
        Window owner, string title, string message,
        string confirmLabel = "OK", string cancelLabel = "Cancel")
    {
        var dlg = new ConfirmDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.ConfirmButton.Content = confirmLabel;
        dlg.CancelButton.Content = cancelLabel;
        dlg.CancelButton.Focus();   // safe default — Enter is the deliberate gesture
        return await dlg.ShowDialog<bool?>(owner) == true;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Close(true);
        else if (e.Key == Key.Escape) Close(false);
        base.OnKeyDown(e);
    }
}
