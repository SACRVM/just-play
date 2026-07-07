using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JustPlay.UI.Behaviors;

/// <summary>
/// Suite-wide TAB suppression. Avalonia's built-in <c>KeyboardNavigationHandler</c> moves focus on
/// Tab (with the keyboard focus ring) from a handler on the window that runs FIRST in the tunnel and
/// does NOT honor <c>e.Handled</c> — so it can't be preempted by adding our own tunnel handler
/// (verified against the release/12.0.3 source: <c>OnKeyDown</c> sets <c>e.Handled = Move(...)</c>
/// only when Tab finds a next control, which is exactly why arrow keys still reach our handlers but
/// Tab doesn't).
///
/// This behavior instead runs AFTER it (<c>handledEventsToo: true</c>) and RESTORES focus to the
/// element that actually had it (the key event's source), so Tab becomes a no-op: no traversal, no
/// ring. The restore is synchronous within the same event, so no frame renders between the two focus
/// changes and the ring never flashes.
///
/// Enabled suite-wide via a <c>:is(Window)</c> style in JustStyles. A view that REPURPOSES Tab (e.g.
/// the PRE CUE FINDER's folders/files switch) opts out with <c>beh:KeyboardNav.SuppressTab="False"</c>
/// and handles Tab in its own handler. Chloe 2026-07-06: "default Tab raus — aus allen Apps / Sichten
/// — komplett unterbinden."
/// </summary>
public static class KeyboardNav
{
    public static readonly AttachedProperty<bool> SuppressTabProperty =
        AvaloniaProperty.RegisterAttached<TopLevel, bool>("SuppressTab", typeof(KeyboardNav));

    public static void SetSuppressTab(TopLevel element, bool value) => element.SetValue(SuppressTabProperty, value);
    public static bool GetSuppressTab(TopLevel element) => element.GetValue(SuppressTabProperty);

    static KeyboardNav()
    {
        SuppressTabProperty.Changed.AddClassHandler<TopLevel>((top, e) =>
        {
            top.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (e.GetNewValue<bool>())
                top.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        });
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab) return;
        // KeyboardNavigationHandler already moved focus in this same tunnel pass — put it straight back.
        (e.Source as IInputElement)?.Focus();
        e.Handled = true;
    }
}
