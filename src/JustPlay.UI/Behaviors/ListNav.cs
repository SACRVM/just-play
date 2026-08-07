using System;
using Avalonia.Controls;

namespace JustPlay.UI.Behaviors;

/// <summary>
/// Keyboard cursor movement for a <see cref="ListBox"/> - the arrow / page / home-end half of the
/// suite's two-pane browsing.
///
/// <para>It works on the CONTROL rather than on a view model on purpose. The PRE CUE FINDER drives
/// its cursor through its own view model (<c>MoveSelection</c> / <c>MoveFolderSelection</c>), which
/// works but ties the movement to one specific VM shape; JUST TAG's file list cannot do the same,
/// because its selection is guarded by the unsaved-edits question in the view. Everything both of
/// them actually need is "move the cursor in this list", and that is a property of the list.</para>
///
/// <para>Selection is set through <see cref="SelectingItemsControl.SelectedIndex"/>, so Avalonia's
/// own AutoScrollToSelectedItem brings the row into view - no manual ScrollIntoView, and the
/// SelectionChanged handlers a host already has keep firing exactly as they do for a mouse click.</para>
/// </summary>
public static class ListNav
{
    /// <summary>Rows a PageUp / PageDown moves. A fixed step rather than a measured viewport: the
    /// finder has used this number since it was built, and "one page" being a stable, predictable
    /// distance matters more here than matching the window height.</summary>
    public const int PageStep = 10;

    /// <summary>Move the cursor by <paramref name="delta"/> rows, clamped. An empty list is a no-op.
    /// With nothing selected yet, a downward move lands on the first row and an upward move on the
    /// last - the same "start at the near end" behaviour every file manager has.</summary>
    public static void Move(ListBox? list, int delta)
    {
        if (list is null || list.ItemCount == 0 || delta == 0) return;

        var index = list.SelectedIndex;
        if (index < 0) { MoveTo(list, delta > 0 ? 0 : int.MaxValue); return; }

        MoveTo(list, index + delta);
    }

    /// <summary>Jump to an absolute row, clamped - <c>0</c> for Home, <see cref="int.MaxValue"/> for End.</summary>
    public static void MoveTo(ListBox? list, int index)
    {
        if (list is null || list.ItemCount == 0) return;
        list.SelectedIndex = Math.Clamp(index, 0, list.ItemCount - 1);
    }
}
