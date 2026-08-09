using System;
using System.Text;

namespace JustPlay.Core.Tagging;

/// <summary>
/// What to do to the text that is already in a field. One entry per operation, because the UI picks
/// exactly one at a time - a transform that did two things at once could not be previewed as one
/// answer.
/// </summary>
public enum TextOperation
{
    /// <summary>Find one piece of text and put another in its place.</summary>
    Replace,

    /// <summary>everything small.</summary>
    Lowercase,

    /// <summary>EVERYTHING BIG.</summary>
    Uppercase,

    /// <summary>Every Word Starts With A Capital - see the rule in <see cref="TextTransform"/>.</summary>
    TitleCase,

    /// <summary>One capital at the front, the rest small.</summary>
    SentenceCase,

    /// <summary>Collapse runs of whitespace into one space and trim the ends.</summary>
    Tidy,
}

/// <summary>
/// Transforming the text a field ALREADY holds - the other half of a tag editor, and the half a DJ
/// reaches for first on a freshly downloaded folder: "PERC - GOB" wants to read "Perc - Gob", an
/// underscore wants to be a space, and a site name wants to come out of the comment.
///
/// <para>Pure functions on strings, so the behaviour that decides what lands in hundreds of files is
/// arithmetic that can be pinned by a test rather than something to be eyeballed once in a window.
/// Null in, null out: a field with nothing in it has nothing to transform, and must not come back as
/// an empty string that then reads as "clear this".</para>
///
/// <para><b>Culture.</b> Every comparison here is ORDINAL and every case change is INVARIANT, on
/// purpose. The app ships with <c>InvariantGlobalization=true</c>, so a culture-sensitive call would
/// quietly be the invariant one anyway - saying so in the code means the next reader does not have to
/// work that out. It is also the only behaviour that is the SAME on every machine: a Turkish locale
/// lowercases "I" to a dotless i, and a tag written on one laptop must not differ from the same tag
/// written on another. Ordinal matters for the same reason - find-and-replace has to match what you
/// can see in the box, character for character, not what a collation considers equivalent.</para>
///
/// <para><b>THE TITLE-CASE RULE</b> (the one every tagger embarrasses itself on):</para>
/// <para>
/// <c>Every letter is lowercased. A letter is then capitalised when it STARTS A WORD, and a word
/// starts wherever the character before it is not a letter, not a digit and not an apostrophe.</c>
/// </para>
/// <para>
/// That is the whole rule. It keeps NOTHING - no dictionary of exceptions, no "this looks like an
/// acronym" guess, no list of small words that stay small. The result is:
/// </para>
/// <list type="bullet">
///   <item><c>PERC - GOB</c> -> <c>Perc - Gob</c> (the case this exists for)</item>
///   <item><c>remix (VIP Mix)</c> -> <c>Remix (Vip Mix)</c> - a bracket starts a word</item>
///   <item><c>AC-DC</c> -> <c>Ac-Dc</c> - a hyphen starts a word, which is what gets
///         <c>hi-fi</c> and <c>non-stop</c> right</item>
///   <item><c>DJ Hidden</c> -> <c>Dj Hidden</c>, <c>feat.</c> -> <c>Feat.</c></item>
///   <item><c>o'brien</c> -> <c>O'brien</c> - an apostrophe is INSIDE a word, which is what gets
///         <c>don't</c> right, and <c>don't</c> is a thousand times more common than <c>O'Brien</c></item>
///   <item><c>3rd</c> stays <c>3rd</c> and <c>mp3</c> becomes <c>Mp3</c> - a digit is inside a word</item>
/// </list>
/// <para>
/// So an acronym comes back as <c>Vip</c>, <c>Dj</c>, <c>Uk</c>. That is a deliberate trade, not an
/// oversight: the alternative is a tagger that is clever in a way you cannot predict, and a rule you
/// cannot predict is worse than one that is dumb and consistent - you cannot tell in advance which of
/// your 400 files it will decide to leave alone. Keeping ALL-CAPS words would also refuse the one
/// case this feature was built for, since <c>PERC - GOB</c> is all caps too. The way back is the
/// operation sitting right next to it: Title Case, then Replace <c>Vip</c> -> <c>VIP</c>. Two passes,
/// both previewed, both predictable.
/// </para>
/// </summary>
public static class TextTransform
{
    // Character codes, not escapes - this file is ASCII by rule (SourceIsAsciiTests), and a code
    // point says which character is meant without anyone counting backslashes.
    private const char Apostrophe = (char)0x27;    // '
    private const char RightQuote = (char)0x2019;  // the typographic apostrophe a download often carries

    /// <summary>
    /// Run one operation over one value.
    /// </summary>
    /// <param name="op">Which transform.</param>
    /// <param name="text">The value as it stands. Null and empty come back unchanged.</param>
    /// <param name="find">Only for <see cref="TextOperation.Replace"/>.</param>
    /// <param name="with">Only for <see cref="TextOperation.Replace"/>; null means "take it out".</param>
    /// <param name="matchCase">Only for <see cref="TextOperation.Replace"/>.</param>
    public static string? Apply(TextOperation op, string? text,
                                string? find = null, string? with = null, bool matchCase = false) =>
        op switch
        {
            TextOperation.Replace      => Replace(text, find, with, matchCase),
            TextOperation.Lowercase    => ToLower(text),
            TextOperation.Uppercase    => ToUpper(text),
            TextOperation.TitleCase    => ToTitle(text),
            TextOperation.SentenceCase => ToSentence(text),
            TextOperation.Tidy         => Tidy(text),
            _                          => text,
        };

    /// <summary>
    /// Does this operation have everything it needs? Only <see cref="TextOperation.Replace"/> can be
    /// half-filled - without something to find it is not a transform, it is an empty box, and running
    /// it would report "nothing changes" as though that were an answer.
    /// </summary>
    public static bool IsUsable(TextOperation op, string? find) =>
        op != TextOperation.Replace || !string.IsNullOrEmpty(find);

    /// <summary>
    /// Put <paramref name="with"/> wherever <paramref name="find"/> appears. An empty
    /// <paramref name="with"/> takes the text out - which is how a site name comes out of a comment.
    /// <para>Nothing to find means nothing happens, rather than an exception: the box is empty while
    /// you are still typing into it.</para>
    /// </summary>
    public static string? Replace(string? text, string? find, string? with, bool matchCase)
    {
        if (text is null || string.IsNullOrEmpty(find)) return text;

        return text.Replace(find, with ?? "",
                            matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    public static string? ToLower(string? text) => text?.ToLowerInvariant();

    public static string? ToUpper(string? text) => text?.ToUpperInvariant();

    /// <summary>Every Word Starts With A Capital. The rule, in full, is in the class summary - read it
    /// before changing a character of this.</summary>
    public static string? ToTitle(string? text)
    {
        if (text is null) return null;

        var sb = new StringBuilder(text.Length);
        var startsWord = true;

        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(startsWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                startsWord = false;
                continue;
            }

            sb.Append(c);

            // A digit or an apostrophe is INSIDE a word ("3rd", "don't"); everything else - space,
            // dash, bracket, dot, underscore, slash - ends one.
            startsWord = !(char.IsDigit(c) || c == Apostrophe || c == RightQuote);
        }

        return sb.ToString();
    }

    /// <summary>
    /// One capital at the front, the rest small.
    /// <para>Exactly ONE, deliberately: a tag field holds a title or a name, not prose, so hunting for
    /// full stops to capitalise after would find the one in "feat." and the one in "Vol. 2" long
    /// before it found a sentence. The capital goes on the first LETTER, so a value that opens with a
    /// bracket or a number still gets one.</para>
    /// </summary>
    public static string? ToSentence(string? text)
    {
        if (text is null) return null;

        var sb = new StringBuilder(text.ToLowerInvariant());
        for (var i = 0; i < sb.Length; i++)
        {
            if (!char.IsLetter(sb[i])) continue;
            sb[i] = char.ToUpperInvariant(sb[i]);
            break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Collapse every run of whitespace into ONE plain space and trim the ends.
    /// <para>Any whitespace character becomes a plain space - a tab, a line break, a non-breaking
    /// space. Those are what a copy-and-paste out of a web page leaves behind, they are invisible in
    /// a field, and they are why two tags that look identical sort apart.</para>
    /// </summary>
    public static string? Tidy(string? text)
    {
        if (text is null) return null;

        var sb = new StringBuilder(text.Length);
        var gap = false;      // whitespace seen, not yet written - dropped entirely at the ends
        var started = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                gap = started;
                continue;
            }

            if (gap) { sb.Append(' '); gap = false; }
            sb.Append(c);
            started = true;
        }

        return sb.ToString();
    }
}
