using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace JustPlay.UI.Controls;

/// <summary>
/// Strip an <see cref="AutoCompleteBox"/>'s INNER text box of its own chrome.
///
/// <para>(!) Why this needs code at all. The suite styles a focused box with
/// <c>TextBox:focus /template/ Border#PART_BorderElement</c> - a 1 px ring - and that selector hits
/// every TextBox in the application, including the one inside AutoCompleteBox's template. That inner
/// box carries no classes, so <c>TextBox.bare</c> cannot reach it, and Avalonia has no second
/// <c>/template/</c> hop to get past its own template boundary. The result was a box that drew a
/// SECOND border inside the field the moment it took focus - the field's own
/// <c>Border.fieldbox:focus-within</c> ring plus the stock one, nested.</para>
///
/// <para>So the class is put on from here, once, when the template appears. After that the ordinary
/// <c>TextBox.bare</c> rules apply to it like any other input and the field has exactly one ring.</para>
/// </summary>
public static class BareInput
{
    /// <summary>Give <paramref name="box"/>'s inner TextBox the <c>bare</c> class as soon as it
    /// exists. Safe to call more than once.</summary>
    public static void Apply(AutoCompleteBox box)
    {
        if (box is null) return;

        box.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<TextBox>("PART_TextBox") is not { } inner) return;
            if (!inner.Classes.Contains("bare")) inner.Classes.Add("bare");
        };
    }
}
