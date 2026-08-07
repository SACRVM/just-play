using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace JustPlay.UI.Controls;

/// <summary>
/// Shared helpers for the frameless-window chrome (every JUST suite app). A window is
/// dragged from empty chrome surfaces but must NOT start a drag when the press lands on
/// an interactive control (a button or a slider) - this is the common predicate.
/// </summary>
public static class WindowChrome
{
    /// <summary>
    /// The whole chrome-bar press gesture, in one place: a press on empty chrome drags the window, a
    /// DOUBLE press maximizes or restores it, and a press that landed on a control does neither.
    ///
    /// <para>Double-click-to-maximize is what every title bar does, and this suite simply never had it
    /// - all five windows went straight to <c>BeginMoveDrag</c> (Chloe 2026-08-05). It is written here
    /// rather than five times, because five copies is how three of them would end up with it.</para>
    ///
    /// <para>A window that is not an <see cref="IFramelessWindow"/> (a fixed-size dialog) simply does
    /// not react to the double press - there is nothing sensible for it to maximize into.</para>
    /// </summary>
    public static void HandlePress(Window? window, PointerPressedEventArgs e)
    {
        if (window is null) return;
        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
        if (IsInteractive(e.Source as Visual)) return;

        if (e.ClickCount == 2)
        {
            (window as IFramelessWindow)?.ToggleMaximize();
            return;
        }

        window.BeginMoveDrag(e);
    }

    /// <summary>True if <paramref name="v"/> or any visual ancestor is an interactive control
    /// (Button / RangeBase) that should swallow the press instead of starting a window drag.</summary>
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
