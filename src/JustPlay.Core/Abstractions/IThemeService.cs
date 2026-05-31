using JustPlay.Core.Theming;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Applies a theme palette to the running application. Implementations live
/// in the UI layer because the act of "applying" means overwriting
/// framework-specific resources (Avalonia <c>Application.Current.Resources</c>
/// today, conceptually the same on any future UI runtime).
/// </summary>
public interface IThemeService
{
    /// <summary>The theme currently in effect.</summary>
    Theme Current { get; }

    /// <summary>
    /// Switch to a different theme. Fires <see cref="ThemeChanged"/> after
    /// the swap is visible. No-op when <paramref name="theme"/> equals
    /// <see cref="Current"/>.
    /// </summary>
    void Apply(Theme theme);

    event EventHandler<Theme>? ThemeChanged;
}
