using Avalonia.Controls;

namespace JustPlay.App.Controls;

/// <summary>
/// Slide-in tweaks panel (theme palette · stage toggles · audio), shared as a self-contained
/// control. Binds to the inherited <c>MainWindowViewModel</c> DataContext; the consumer controls
/// visibility and docking. (About moved to the title-bar brand; compact layout has its own button.)
/// </summary>
public partial class TweaksPanel : UserControl
{
    public TweaksPanel() => InitializeComponent();
}
