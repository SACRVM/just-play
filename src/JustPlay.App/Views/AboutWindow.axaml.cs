using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace JustPlay.App.Views;

/// <summary>
/// Themed "About JustPlay" dialog. No view-model — the only dynamic value is the
/// version string, filled from <see cref="AppInfo"/> here. Opens modally over the
/// main window (see TweaksPanel.OnAboutClick).
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

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
