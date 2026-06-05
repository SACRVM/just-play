using Avalonia.Controls;
using Avalonia.Interactivity;
using JustPlay.App.Views;

namespace JustPlay.App.Controls;

/// <summary>
/// Slide-in tweaks panel (theme palette · stage toggles · audio · about), shared as
/// a self-contained control. Binds to the inherited <c>MainWindowViewModel</c>
/// DataContext; the consumer controls visibility and docking.
/// </summary>
public partial class TweaksPanel : UserControl
{
    public TweaksPanel()
    {
        InitializeComponent();
        VersionLabel.Text = AppInfo.DisplayVersion;
    }

    // About row → open the themed About dialog modally over the main window.
    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            new AboutWindow().ShowDialog(owner);
    }
}
