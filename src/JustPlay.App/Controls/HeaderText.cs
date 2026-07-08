using Avalonia;
using Avalonia.Controls;

namespace JustPlay.App.Controls;

/// <summary>
/// The final fix for Avalonia's trailing letter-spacing on header labels. Avalonia (like CSS)
/// adds the letter-spacing gap AFTER the last glyph too, so a right- or centre-aligned *tracked*
/// label sits ~LetterSpacing px short of its cell's right edge — visibly misaligned with the
/// untracked numeric values in the rows below it. Chloe 2026-07-08.
///
/// Setting <c>controls:HeaderText.FlushTrailing="True"</c> (once, in the .colhead / .finderhead
/// style) reclaims exactly the element's OWN LetterSpacing from its right margin, so the visible
/// ink ends flush with the cell edge again. It is SINGLE-SOURCE: the compensation is derived from
/// <see cref="TextBlock.LetterSpacing"/>, so changing the tracking in one place keeps the flush
/// correct — there is no second literal to keep in sync (which was the whole complaint). It is a
/// no-op when LetterSpacing ≤ 0 (e.g. the sort arrows, which carry their own margin), so it never
/// fights an untracked element's layout.
///
/// Result, exactly to spec: unsorted → the label is the right-most element and sits flush (no gap);
/// sorted → the LetterSpacing-0 arrow is the right-most element (also flush) and the label's trailing
/// space becomes the gap *between* label and arrow. The gap hangs on the arrow, not on the text.
/// </summary>
public static class HeaderText
{
    public static readonly AttachedProperty<bool> FlushTrailingProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("FlushTrailing", typeof(HeaderText));

    public static bool GetFlushTrailing(TextBlock t) => t.GetValue(FlushTrailingProperty);
    public static void SetFlushTrailing(TextBlock t, bool value) => t.SetValue(FlushTrailingProperty, value);

    static HeaderText()
    {
        // Re-apply whenever the flag is (un)set OR the letter-spacing changes (styles set both at
        // load; either order converges). Changing Margin never changes LetterSpacing, so no loop.
        FlushTrailingProperty.Changed.AddClassHandler<TextBlock>((t, _) => Apply(t));
        TextBlock.LetterSpacingProperty.Changed.AddClassHandler<TextBlock>((t, _) =>
        {
            if (GetFlushTrailing(t)) Apply(t);
        });
    }

    private static void Apply(TextBlock t)
    {
        var ls = GetFlushTrailing(t) ? t.LetterSpacing : 0;
        var right = ls > 0 ? -ls : 0;          // reclaim only when we actually add tracking
        var m = t.Margin;
        if (m.Right != right)
            t.Margin = new Thickness(m.Left, m.Top, right, m.Bottom);
    }
}
