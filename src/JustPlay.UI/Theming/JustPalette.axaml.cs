using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace JustPlay.UI.Theming;

/// <summary>
/// The canonical J.U.S.T. suite theme palette as a typed, mergeable
/// <see cref="ResourceDictionary"/>. Every JUST app merges it into
/// <c>Application.Resources</c> so the design tokens (theme colours, derived
/// brushes, text ramp, ToggleSwitch overrides, palette swatches) are defined
/// ONCE. AvaloniaThemeService overwrites the nine theme Colour keys at runtime
/// to switch palettes live. See <see cref="AvaloniaThemeService"/>.
/// </summary>
public partial class JustPalette : ResourceDictionary
{
    public JustPalette() => AvaloniaXamlLoader.Load(this);
}
