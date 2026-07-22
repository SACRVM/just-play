using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JustPlay.App.Views;

/// <summary>
/// Tiny themed yes/no confirmation dialog. <see cref="AskAsync"/> shows it modally over an owner
/// window and returns true when the user confirms. Enter confirms, Esc cancels. Mirrors
/// <see cref="InputDialog"/>'s frameless themed-card look.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    /// <summary>Ask a yes/no question. Returns true when confirmed, false on cancel / Esc / close.</summary>
    public static async Task<bool> AskAsync(Window owner, string title, string message, string confirmLabel = "OK")
    {
        var dlg = new ConfirmDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.ConfirmButton.Content = confirmLabel;
        return await dlg.ShowDialog<bool>(owner);
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
