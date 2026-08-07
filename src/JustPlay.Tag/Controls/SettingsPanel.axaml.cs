using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using JustPlay.Tag.ViewModels;

namespace JustPlay.Tag.Controls;

/// <summary>
/// JUST TAG's settings, as the slide-in panel on the right rather than a separate window
/// (Chloe 2026-08-06). Its own <see cref="SettingsViewModel"/> DataContext is assigned by the host,
/// so the panel stays independent of the window's TaggerViewModel - the same split the JUST PLAY
/// tweaks sidebar has, just with two view models instead of one.
/// </summary>
public partial class SettingsPanel : UserControl
{
    public SettingsPanel() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Closing is the HOST's decision (it owns the open/closed flag), so the panel just
    /// says it was asked to close.</summary>
    public event System.Action? CloseRequested;

    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
