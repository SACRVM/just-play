using System.Text.RegularExpressions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>
/// Builds and strips the "DJ Software compatible" comment segment that JustPlay
/// prepends to a file's comment tag. The segment format is:
/// <c>{Camelot} - Energy {energy}</c> (e.g. <c>8A - Energy 7</c>),
/// with degenerate forms when only key or only energy is known.
///
/// On write the segment is placed at the START of the existing comment; the
/// user's own text is kept, joined as <c>"{segment} | {userText}"</c>.
///
/// Idempotent: if the comment already starts with a JustPlay segment the old
/// one is stripped before the new one is prepended, so writing twice never
/// doubles the prefix.
/// </summary>
public static class DjCommentBuilder
{
    // Matches a JustPlay-generated prefix at the start of a comment string:
    //   Camelot optional  +  "- Energy N"  optional  (but at least one must match)
    // followed by an optional " | " separator before any user text.
    //
    // Examples it must match as the full "our segment":
    //   "8A - Energy 7"
    //   "8A"
    //   "Energy 7"
    //   "8A - Energy 7 | my note"   → segment = "8A - Energy 7", rest = "my note"
    //   "8A | my note"              → segment = "8A",            rest = "my note"
    //
    // The Camelot token is 1–2 digits + A or B (case-insensitive).
    private static readonly Regex PrefixPattern = new(
        @"^(?:(?<camelot>\d{1,2}[ABab])(?:\s*-\s*Energy\s+\d+)?|Energy\s+\d+)(?:\s*\|\s*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Build the full comment string for a tag write.
    /// </summary>
    /// <param name="key">Detected musical key, or null.</param>
    /// <param name="energy">Detected energy (1-10), or null.</param>
    /// <param name="existingComment">The raw comment currently in the file (may already contain
    /// a JustPlay prefix from a previous write — it will be stripped first).</param>
    /// <returns>
    /// The new comment to write, or null if there is nothing to write (no key and no energy —
    /// caller should leave the comment untouched).
    /// </returns>
    public static string? Build(MusicalKey? key, int? energy, string? existingComment)
    {
        var segment = BuildSegment(key, energy);
        if (segment is null) return null; // nothing to prepend — no-op

        // Strip any existing JustPlay prefix so re-write is idempotent.
        var userText = Strip(existingComment);

        return string.IsNullOrEmpty(userText)
            ? segment
            : $"{segment} | {userText}";
    }

    /// <summary>
    /// Strip a JustPlay-generated prefix (if any) from the front of a comment string and
    /// return the remainder (the user's own text). Returns the input unchanged if no prefix
    /// is present; returns an empty string if the entire comment was our segment.
    ///
    /// Two legacy prefix shapes are removed:
    ///   1. the machine-readable <c>JP|E7|K8A|bpm140|gc.57|…</c> vibe blob that an early
    ///      CLI batch ("tag write") prepended to comments — NOT human-facing, so it is
    ///      stripped and the comment rebuilt clean (the vibe data lives in the JUSTPLAY
    ///      blob + index, never in the comment);
    ///   2. the MIK-style <c>8A - Energy 7</c> segment this builder writes.
    /// Both are removed (JP first, then MIK) so a re-write is fully idempotent regardless
    /// of which writer touched the file last.
    /// </summary>
    public static string Strip(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";
        comment = StripJpBlob(comment);
        if (string.IsNullOrEmpty(comment)) return "";
        var m = PrefixPattern.Match(comment);
        return m.Success ? comment[m.Length..].TrimStart() : comment;
    }

    /// <summary>
    /// Strip a legacy <c>JP|…</c> vibe blob from the front of a string and return the
    /// remainder (user text after the <c>" | "</c> separator). Returns the input unchanged
    /// when there is no JP prefix; returns "" when the whole string was the blob.
    /// The blob's own field separator is a bare <c>|</c>; user text is joined with <c>" | "</c>
    /// (spaces), so the first <c>" | "</c> marks the end of our block.
    /// </summary>
    private static string StripJpBlob(string comment)
    {
        if (comment != "JP" && !comment.StartsWith("JP|", System.StringComparison.Ordinal))
            return comment;
        var sep = comment.IndexOf(" | ", System.StringComparison.Ordinal);
        return sep >= 0 ? comment[(sep + 3)..] : "";
    }

    /// <summary>Build only the DJ-segment token (no user text), or null when there is nothing
    /// to emit (both key and energy are absent).</summary>
    public static string? BuildSegment(MusicalKey? key, int? energy)
    {
        if (key is null && energy is null) return null;

        if (key is not null && energy is not null)
            return $"{key.Value.Camelot} - Energy {energy}";

        if (key is not null)
            return key.Value.Camelot;

        return $"Energy {energy}";
    }
}
