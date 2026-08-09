using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustPlay.UI.Controls;

namespace JustPlay.App.Views;

/// <summary>
/// Copyable crash/error dialog shown by <see cref="ErrorReporter"/>. Read-only report + a Copy button
/// that puts the whole thing on the clipboard so the user can paste it to us. Esc / Close dismisses.
/// </summary>
public partial class OopsDialog : Window
{
    public OopsDialog() => InitializeComponent();

    public OopsDialog(string report) : this() => Details.Text = report;

    // (!!) Of every clipboard call in the suite, THIS is the one that must not throw: it is the
    // button on the dialog that is already showing you a crash. It used to call the clipboard
    // straight out of an `async void` handler, so a clipboard the OS refused would have taken the
    // process down while reporting that the process had a problem.
    private async void OnCopy(object? sender, RoutedEventArgs e) =>
        CopiedHint.IsVisible = await SystemClipboard.CopyTextAsync(this, Details.Text);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
