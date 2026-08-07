using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace JustPlay.App.Views;

/// <summary>
/// Copyable crash/error dialog shown by <see cref="ErrorReporter"/>. Read-only report + a Copy button
/// that puts the whole thing on the clipboard so the user can paste it to us. Esc / Close dismisses.
/// </summary>
public partial class OopsDialog : Window
{
    public OopsDialog() => InitializeComponent();

    public OopsDialog(string report) : this() => Details.Text = report;

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        // Window is a TopLevel -> its Clipboard. Null on some headless setups; guard it.
        // Avalonia 12 dropped IClipboard.SetTextAsync -> wrap the text in a DataTransfer (verified
        // against release/12.0.3 source: DataTransferItem.CreateText + IClipboard.SetDataAsync).
        if (Clipboard is { } cb)
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(Details.Text ?? string.Empty));
            await cb.SetDataAsync(data);
            CopiedHint.IsVisible = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
