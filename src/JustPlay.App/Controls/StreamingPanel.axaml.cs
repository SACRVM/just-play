using Avalonia.Controls;

namespace JustPlay.App.Controls;

/// <summary>
/// Slide-in Streaming panel - Icecast server profile manager (S1 UI: add / edit / delete / select).
/// Binds to the inherited <c>MainWindowViewModel</c> DataContext; the consumer controls visibility
/// and docking. The "Connect" button inside is a clearly-labelled stub until the streaming engine
/// (JustPlay.Streaming, S2) is wired in.
/// </summary>
public partial class StreamingPanel : UserControl
{
    public StreamingPanel() => InitializeComponent();
}
