using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace JustPlay.UI.Controls;

/// <summary>
/// Shared J.U.S.T. keyboard-hint bar - the chip+label legend row (Space - Tab - ... or C - R) shown at
/// the bottom of the PRE CUE FINDER and JUST STREAM. One implementation, one look; each host binds its
/// own <see cref="Hints"/> and controls visibility (e.g. a Settings toggle). Chip styling lives in the
/// shared Border.kbd / TextBlock.kbdlabel classes (JustStyles).
/// </summary>
public partial class KeyLegend : UserControl
{
    public static readonly StyledProperty<IEnumerable?> HintsProperty =
        AvaloniaProperty.Register<KeyLegend, IEnumerable?>(nameof(Hints));

    /// <summary>The <see cref="KeyHint"/> entries to display, in order.</summary>
    public IEnumerable? Hints
    {
        get => GetValue(HintsProperty);
        set => SetValue(HintsProperty, value);
    }

    public KeyLegend() => InitializeComponent();
}

/// <summary>One legend entry: the key-cap text and what it does.</summary>
public sealed record KeyHint(string Key, string Label);
