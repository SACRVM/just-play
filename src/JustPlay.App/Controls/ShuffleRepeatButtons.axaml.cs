using Avalonia.Controls;

namespace JustPlay.App.Controls;

/// <summary>
/// Shuffle + repeat transport toggles, shared by MaxView and MiniView. Pure
/// markup - binds to the inherited <c>MainWindowViewModel</c> DataContext.
/// </summary>
public partial class ShuffleRepeatButtons : UserControl
{
    public ShuffleRepeatButtons() => InitializeComponent();
}
