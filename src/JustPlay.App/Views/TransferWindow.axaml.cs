using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using JustPlay.App.ViewModels;

namespace JustPlay.App.Views;

/// <summary>
/// Themed progress dialog for a ZIP / folder export. Bound to <see cref="TransferViewModel"/>.
/// Dismiss simply closes the window — the export is a detached Task that keeps running. Cancel trips
/// the view-model's token (the Core writer aborts and deletes its partial output), then closes.
/// </summary>
public partial class TransferWindow : Window
{
    public TransferWindow() => InitializeComponent();

    private void OnDismiss(object? sender, RoutedEventArgs e) => Close();

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        (DataContext as TransferViewModel)?.Cancel();
        Close();
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
        if (e.Key == Key.Escape) Close(); // Esc = Dismiss (background); use the Cancel button to abort.
        base.OnKeyDown(e);
    }
}
