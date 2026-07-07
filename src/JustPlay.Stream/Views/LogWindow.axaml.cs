using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JustPlay.Stream.ViewModels;
using JustPlay.UI.Behaviors;

namespace JustPlay.Stream.Views;

/// <summary>
/// The JUST STREAM event-log window — a frameless rounded themed card (same shell as the shared
/// AboutWindow) that hosts the broadcast event log. Opened from the chrome log button; its
/// DataContext is the main <see cref="ViewModels.StreamViewModel"/> so it shares LogEntries +
/// the CLEAR command.
/// </summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();

        WindowPlacement.Track(this, "JustStream.Log");
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Copy the WHOLE log to the clipboard. (Per-line copy = select text in the box + Ctrl+C.)
    // Avalonia 12 dropped IClipboard.SetTextAsync → wrap the text in a DataTransfer (same pattern as
    // OopsDialog: DataTransferItem.CreateText + IClipboard.SetDataAsync).
    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } cb) return;
        var text = (DataContext as StreamViewModel)?.LogText ?? string.Empty;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        await cb.SetDataAsync(data);
        CopiedHint.IsVisible = true;
    }

    // Frameless window: drag it by the card, but let button clicks through.
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
        BeginMoveDrag(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
