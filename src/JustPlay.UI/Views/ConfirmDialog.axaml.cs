using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JustPlay.UI.Views;

/// <summary>What the user chose when asked about unsaved work.</summary>
public enum SaveChoice
{
    /// <summary>Stay where we are and change nothing — the safe answer, and the one a stray
    /// keystroke or a click outside must land on.</summary>
    Cancel,

    /// <summary>Write the edits, then continue.</summary>
    Save,

    /// <summary>Throw the edits away and continue.</summary>
    Discard,
}

/// <summary>
/// Shared tiny themed confirmation dialog for the J.U.S.T. suite — the counterpart
/// to <see cref="InputDialog"/> for actions that would discard work ("New project?",
/// destructive resets). <see cref="AskAsync"/> shows it modally and returns true only on
/// an explicit confirm; Esc / Cancel / closing = false. Enter confirms, Esc cancels.
/// <para><see cref="AskSaveDiscardCancelAsync"/> is the three-way variant for unsaved edits.</para>
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

    /// <summary>
    /// Ask about unsaved edits: Save / Discard / Cancel. Anything that is not an explicit Save or
    /// Discard — Esc, the close button, clicking away — is <see cref="SaveChoice.Cancel"/>, because
    /// the only harmless answer to "you have unsaved work" is to leave it alone.
    /// </summary>
    public static async Task<SaveChoice> AskSaveDiscardCancelAsync(
        Window owner, string title, string message,
        string saveLabel = "Save", string discardLabel = "Discard", string cancelLabel = "Cancel")
    {
        var dlg = new ConfirmDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;
        dlg.ConfirmButton.Content = saveLabel;
        dlg.MiddleButton.Content = discardLabel;
        dlg.MiddleButton.IsVisible = true;
        dlg.CancelButton.Content = cancelLabel;
        dlg._threeWay = true;
        dlg.ConfirmButton.Focus();  // Save is the likely intent when you asked to leave a dirty file
        return await dlg.ShowDialog<SaveChoice?>(owner) ?? SaveChoice.Cancel;
    }

    /// <summary>Which result type this instance closes with — the two entry points are answered by
    /// different callers awaiting different types, so the close value has to match the question.</summary>
    private bool _threeWay;

    private void OnConfirm(object? sender, RoutedEventArgs e) => CloseWith(true);

    private void OnMiddle(object? sender, RoutedEventArgs e) => Close(SaveChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => CloseWith(false);

    private void CloseWith(bool confirmed)
    {
        if (_threeWay) Close(confirmed ? SaveChoice.Save : SaveChoice.Cancel);
        else Close(confirmed);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CloseWith(true);
        else if (e.Key == Key.Escape) CloseWith(false);
        base.OnKeyDown(e);
    }
}
