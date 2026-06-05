using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.Core.Updates;

namespace JustPlay.App.Views;

/// <summary>
/// Themed "update available" dialog. <see cref="AskAsync"/> shows it modally over the owner and
/// returns the user's <see cref="Choice"/>. Esc / card-dismiss == <see cref="Choice.Later"/>.
/// </summary>
public partial class UpdateDialog : Window
{
    public enum Choice { Later, Install, Ignore }

    public UpdateDialog() => InitializeComponent();

    /// <summary>
    /// Show the update prompt. <paramref name="canSelfUpdate"/> picks the primary action label and
    /// the explanation copy (silent self-update vs. open the release page in the browser).
    /// </summary>
    public static async Task<Choice> AskAsync(Window owner, Version? current, UpdateInfo info, bool canSelfUpdate)
    {
        var dlg = new UpdateDialog();
        var currentText = current is null ? "?" : current.ToString(3);

        dlg.TitleText.Text = $"Update available — v{info.Version.ToString(3)}";
        dlg.InstalledText.Text = $"v{currentText}";
        dlg.AvailableText.Text = $"v{info.Version.ToString(3)}";
        dlg.PrimaryButton.Content = canSelfUpdate ? "Update & restart" : "Open in browser";
        dlg.ExplainText.Text = canSelfUpdate
            ? "JustPlay will download the update, close, and reopen on the new version. Playback stops while it installs."
            : "This copy can't update itself automatically — the release page will open in your browser so you can download it.";

        var notes = info.ReleaseNotes?.Trim();
        if (!string.IsNullOrEmpty(notes))
        {
            dlg.NotesText.Text = Truncate(notes, 800);
            dlg.NotesBox.IsVisible = true;
        }

        return await dlg.ShowDialog<Choice>(owner);
    }

    private void OnPrimary(object? sender, RoutedEventArgs e) => Close(Choice.Install);

    private void OnIgnore(object? sender, RoutedEventArgs e) => Close(Choice.Ignore);

    private void OnLater(object? sender, RoutedEventArgs e) => Close(Choice.Later);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close(Choice.Later);
        base.OnKeyDown(e);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + " …";
}
