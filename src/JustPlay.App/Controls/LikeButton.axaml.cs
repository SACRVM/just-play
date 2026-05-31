using Avalonia.Controls;

namespace JustPlay.App.Controls;

/// <summary>
/// The "like" heart button, shared by MaxView and MiniView. Decorative for now;
/// binds to the inherited <c>MainWindowViewModel</c> DataContext when wired up.
/// </summary>
public partial class LikeButton : UserControl
{
    public LikeButton() => InitializeComponent();
}
