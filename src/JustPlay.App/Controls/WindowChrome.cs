using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace JustPlay.App.Controls;

/// <summary>
/// Shared helpers for the frameless-window chrome. Both views drag the window
/// from empty chrome surfaces but must NOT start a drag when the press lands on
/// an interactive control (a button or a slider) — this is the common predicate.
/// </summary>
public static class WindowChrome
{
    /// <summary>True if <paramref name="v"/> or any visual ancestor is an
    /// interactive control (Button / RangeBase) that should swallow the press
    /// instead of starting a window drag.</summary>
    public static bool IsInteractive(Visual? v)
    {
        while (v is not null)
        {
            if (v is Button or RangeBase) return true;
            v = v.GetVisualParent();
        }
        return false;
    }
}
