using System;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// What the search actually DECIDES, as pure functions over one row and one condition.
///
/// <para>It was private inside <see cref="TaggerViewModel"/> until Chloe reported the filter behaving
/// "inverted" (2026-08-05) — and a search whose behaviour can only be checked by clicking through the
/// app is a search nobody can check. Every mode below is now pinned by a test, so a claim about what
/// "is not" does is a fact and not a reading of the code.</para>
///
/// <para>The rules that are easy to get backwards, stated once:</para>
/// <list type="bullet">
///   <item>An ABSENT field is <c>null</c>, never <c>""</c> and never <c>"0"</c> — otherwise "is empty"
///         would stop finding the very files it exists for.</item>
///   <item>An absent field counts as "does not contain" and as "is not". Otherwise "ID3 version is not
///         2.4" would hide exactly the files that carry no tag at all.</item>
///   <item>An absent field never satisfies a POSITIVE test (contains / is / starts with).</item>
/// </list>
/// </summary>
public static class TagSearch
{
    /// <summary>Modes that compare against typed text. The two emptiness modes do not, which is why
    /// the value box hides for them.</summary>
    public static bool NeedsValue(MatchMode? mode) =>
        mode is not (MatchMode.IsEmpty or MatchMode.IsNotEmpty);

    /// <summary>
    /// Whether a value box is needed for this FIELD + MODE pair. Artwork is the one field with nothing
    /// to type: it is present or it is not, so choosing it means choosing between the two emptiness
    /// modes and the box goes away (Chloe 2026-08-05: "dann blendet man eben text feld aus wenn man
    /// cover wählt").
    /// </summary>
    public static bool NeedsValue(TagField field, MatchMode? mode) =>
        field != TagField.Cover && NeedsValue(mode);

    /// <summary>
    /// A condition only counts once it can actually decide something: an emptiness test needs no text,
    /// everything else does. A condition that is not active is simply not applied — it never narrows
    /// the list to nothing and never widens it to everything.
    /// </summary>
    public static bool IsActive(TagField field, MatchMode? mode, string? value) =>
        !NeedsValue(field, mode) || !string.IsNullOrWhiteSpace(value);

    /// <summary>Does this row satisfy the condition?</summary>
    public static bool Matches(FileRow row, TagField field, MatchMode? mode, string? raw)
    {
        var value = raw?.Trim() ?? "";

        // "All fields" is the "just type something" case: name, title, artist and genre at once.
        // Only the two text modes mean anything across four fields at the same time — asking whether
        // "all fields are empty" is not a question, so those fall back to the broad contains.
        if (field == TagField.All)
            return mode switch
            {
                MatchMode.NotContains => !row.MatchesAnywhere(value),
                _                     => row.MatchesAnywhere(value),
            };

        var text = row.Field(field);
        var has = !string.IsNullOrWhiteSpace(text);

        return (mode ?? MatchMode.Contains) switch
        {
            MatchMode.IsEmpty     => !has,
            MatchMode.IsNotEmpty  => has,

            // Positive tests: an absent field can never satisfy one.
            MatchMode.Contains    => has && text!.Contains(value, StringComparison.OrdinalIgnoreCase),
            MatchMode.Is          => has && string.Equals(text!.Trim(), value, StringComparison.OrdinalIgnoreCase),
            MatchMode.StartsWith  => has && text!.TrimStart().StartsWith(value, StringComparison.OrdinalIgnoreCase),

            // Negative tests: an absent field DOES satisfy one — "genre is not techno" must return the
            // untagged files too, because those are precisely the ones that need fixing.
            MatchMode.NotContains => !(has && text!.Contains(value, StringComparison.OrdinalIgnoreCase)),
            MatchMode.IsNot       => !(has && string.Equals(text!.Trim(), value, StringComparison.OrdinalIgnoreCase)),

            _                     => true,
        };
    }

    /// <summary>Join two conditions. The second is only asked when it is switched on AND active.</summary>
    public static bool Matches(FileRow row,
                               TagField field1, MatchMode? mode1, string? value1,
                               bool hasSecond, bool joinAnd,
                               TagField field2, MatchMode? mode2, string? value2)
    {
        var first = IsActive(field1, mode1, value1) ? Matches(row, field1, mode1, value1) : true;
        if (!hasSecond || !IsActive(field2, mode2, value2)) return first;

        var second = Matches(row, field2, mode2, value2);
        return joinAnd ? first && second : first || second;
    }
}
