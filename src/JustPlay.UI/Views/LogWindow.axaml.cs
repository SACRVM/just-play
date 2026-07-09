using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JustPlay.UI.Controls;
using JustPlay.UI.Logging;

namespace JustPlay.UI.Views;

/// <summary>
/// The SHARED event-log window for the J.U.S.T. suite — a frameless rounded themed card (same shell as the
/// shared AboutWindow). Opened from each app's chrome log button; its DataContext is a <see cref="LogViewModel"/>
/// owned by the app's main view-model. The opener is responsible for <c>WindowPlacement.Track</c> with an
/// app-specific key (this window can't hard-code one — both apps reuse it).
/// </summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();

        // Edge/corner resize for the borderless card (same shared behaviour as the finder / main window),
        // so long error lines can be widened instead of scrolled. Chloe 2026-07-08.
        FramelessResizeBehavior.Attach(this, this.FindControl<Grid>("ResizeGrips")!);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnClearClick(object? sender, RoutedEventArgs e) => (DataContext as LogViewModel)?.Clear();

    // Copy the WHOLE log to the clipboard. (Per-line copy = select text in the box + Ctrl+C.)
    // Avalonia 12 dropped IClipboard.SetTextAsync → wrap the text in a DataTransfer (same pattern as
    // OopsDialog: DataTransferItem.CreateText + IClipboard.SetDataAsync).
    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } cb) return;
        var text = (DataContext as LogViewModel)?.Text ?? string.Empty;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(text));
        await cb.SetDataAsync(data);
        CopiedHint.IsVisible = true;
    }

    // Frameless window: drag it by the card, but let button clicks through.
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<Button>() is not null) return;
        BeginMoveDrag(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
