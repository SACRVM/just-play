using Avalonia.Controls;

namespace JustPlay.App.Controls;

/// <summary>
/// Slide-in tweaks panel (theme palette · stage toggles · default tab), shared as
/// a self-contained control. Binds to the inherited <c>MainWindowViewModel</c>
/// DataContext; the consumer controls visibility and docking.
/// </summary>
public partial class TweaksPanel : UserControl
{
    public TweaksPanel() => InitializeComponent();
}
