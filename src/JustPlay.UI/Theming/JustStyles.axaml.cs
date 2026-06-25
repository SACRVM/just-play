using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace JustPlay.UI.Theming;

/// <summary>
/// The shared J.U.S.T. suite control styles (window chrome caption buttons,
/// underline tabs, hand-cursor affordances, global text drop-shadow) as a typed,
/// includable <see cref="Styles"/> group. Every JUST app adds it to
/// <c>Application.Styles</c> after <c>FluentTheme</c> so the chrome is written ONCE.
/// App-specific styles layer after this include and may extend these classes.
/// </summary>
public partial class JustStyles : Styles
{
    public JustStyles() => AvaloniaXamlLoader.Load(this);
}
