using JustPlay.Core.Tagging;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// The transforms that rewrite text that is already in a tag. This is the half of a tag editor that
/// runs over a whole selection at once, so what it does to one odd string is what it does to four
/// hundred files - pinned here rather than checked by eye once.
///
/// <para>TITLE CASE has a section of its own. Every tagger gets it wrong somewhere; ours is allowed
/// to be wrong in ONE predictable way, and these tests are the statement of which way that is.</para>
/// </summary>
public class TextTransformTests
{
    // -- Replace ---------------------------------------------------------------------------------

    [Fact]
    public void Replace_swaps_every_occurrence()
    {
        Assert.Equal("Perc - Gob - Live",
                     TextTransform.Replace("Perc_-_Gob_-_Live", "_", " ", matchCase: false));
    }

    /// <summary>The reason a DJ opens this window first: a site name baked into every comment.</summary>
    [Fact]
    public void Replace_with_nothing_takes_the_text_out()
    {
        Assert.Equal(" hard techno",
                     TextTransform.Replace("www.somesite.com hard techno", "www.somesite.com", "",
                                           matchCase: false));
    }

    [Fact]
    public void Replace_ignores_case_by_default()
    {
        Assert.Equal("x x", TextTransform.Replace("VIP vip", "vip", "x", matchCase: false));
    }

    [Fact]
    public void Replace_can_be_told_to_match_case()
    {
        Assert.Equal("VIP x", TextTransform.Replace("VIP vip", "vip", "x", matchCase: true));
    }

    /// <summary>Case-insensitive matching still writes the REPLACEMENT as typed - that is what makes
    /// "vip" -> "VIP" a usable way to put an acronym back after a title-case pass.</summary>
    [Fact]
    public void Replace_writes_the_replacement_verbatim()
    {
        Assert.Equal("Hard VIP Mix", TextTransform.Replace("Hard Vip Mix", "vip", "VIP", false));
    }

    [Fact]
    public void Replace_with_nothing_to_find_changes_nothing()
    {
        Assert.Equal("Perc", TextTransform.Replace("Perc", "", "x", false));
        Assert.Equal("Perc", TextTransform.Replace("Perc", null, "x", false));
    }

    [Fact]
    public void Replace_leaves_null_alone()
    {
        Assert.Null(TextTransform.Replace(null, "a", "b", false));
    }

    // -- Upper / lower ---------------------------------------------------------------------------

    [Fact]
    public void Lower_and_upper_do_what_they_say()
    {
        Assert.Equal("perc - gob", TextTransform.ToLower("Perc - GOB"));
        Assert.Equal("PERC - GOB", TextTransform.ToUpper("Perc - gob"));
    }

    /// <summary>Invariant casing, so a tag written on a Turkish-locale machine is the same tag. The
    /// dotted/dotless I is the classic way this goes wrong.</summary>
    [Fact]
    public void Casing_is_invariant()
    {
        Assert.Equal("i", TextTransform.ToLower("I"));
        Assert.Equal("I", TextTransform.ToUpper("i"));
    }

    // -- TITLE CASE - the documented rule --------------------------------------------------------
    //
    // The rule: every letter is lowercased, then a letter is capitalised when it STARTS A WORD, and a
    // word starts wherever the character before it is not a letter, not a digit and not an apostrophe.
    // Nothing is preserved and there is no dictionary. See TextTransform's class summary for why.

    /// <summary>THE case the whole feature exists for.</summary>
    [Fact]
    public void Title_fixes_a_shouting_download()
    {
        Assert.Equal("Perc - Gob", TextTransform.ToTitle("PERC - GOB"));
    }

    [Fact]
    public void Title_capitalises_after_a_bracket()
    {
        Assert.Equal("Remix (Vip Mix)", TextTransform.ToTitle("remix (VIP Mix)"));
    }

    /// <summary>A hyphen starts a word. That is what gets "hi-fi" and "non-stop" right, and it is
    /// also what turns AC-DC into Ac-Dc - the trade is stated, not hidden.</summary>
    [Fact]
    public void Title_capitalises_after_a_hyphen()
    {
        Assert.Equal("Hi-Fi", TextTransform.ToTitle("hi-fi"));
        Assert.Equal("Non-Stop", TextTransform.ToTitle("NON-STOP"));
        Assert.Equal("Ac-Dc", TextTransform.ToTitle("AC-DC"));
    }

    /// <summary>An apostrophe is INSIDE a word, so "don't" survives - and O'Brien does not. That way
    /// round on purpose: contractions are ordinary, Irish surnames in a track title are not.</summary>
    [Fact]
    public void Title_keeps_an_apostrophe_inside_the_word()
    {
        Assert.Equal("Don't Stop", TextTransform.ToTitle("DON'T STOP"));
        Assert.Equal("O'brien", TextTransform.ToTitle("o'brien"));
    }

    [Fact]
    public void Title_keeps_an_apostrophe_inside_the_word_for_the_typographic_one()
    {
        var quote = (char)0x2019;
        Assert.Equal($"Don{quote}t Stop", TextTransform.ToTitle($"DON{quote}T STOP"));
    }

    /// <summary>An acronym comes back mixed-case. Documented, deliberate, and undone with one
    /// Replace pass - which is the operation sitting next to this one in the same window.</summary>
    [Fact]
    public void Title_does_NOT_preserve_an_acronym()
    {
        Assert.Equal("Dj Hidden", TextTransform.ToTitle("DJ HIDDEN"));
        Assert.Equal("Uk Hardcore", TextTransform.ToTitle("UK hardcore"));
    }

    [Fact]
    public void Title_leaves_a_trailing_dot_alone()
    {
        Assert.Equal("Perc Feat. Ansome", TextTransform.ToTitle("perc feat. ansome"));
    }

    /// <summary>A digit is inside a word, so "3rd" stays "3rd" instead of becoming "3Rd".</summary>
    [Fact]
    public void Title_treats_a_digit_as_part_of_the_word()
    {
        Assert.Equal("3rd Movement", TextTransform.ToTitle("3RD MOVEMENT"));
        Assert.Equal("Mp3", TextTransform.ToTitle("MP3"));
    }

    [Fact]
    public void Title_starts_a_word_after_an_underscore_or_a_slash()
    {
        Assert.Equal("Perc_Gob", TextTransform.ToTitle("PERC_GOB"));
        Assert.Equal("Hard Techno/Industrial", TextTransform.ToTitle("HARD TECHNO/INDUSTRIAL"));
    }

    [Fact]
    public void Title_is_idempotent()
    {
        const string messy = "PERC - GOB (ORIGINAL MIX) feat. AC-DC's 3rd";
        var once = TextTransform.ToTitle(messy);
        Assert.Equal(once, TextTransform.ToTitle(once));
    }

    // -- Sentence case ---------------------------------------------------------------------------

    [Fact]
    public void Sentence_capitalises_exactly_one_letter()
    {
        Assert.Equal("Gob (original mix)", TextTransform.ToSentence("GOB (ORIGINAL MIX)"));
    }

    /// <summary>The capital goes on the first LETTER, so a value that opens with a bracket or a
    /// number still gets one.</summary>
    [Fact]
    public void Sentence_skips_past_a_leading_non_letter()
    {
        Assert.Equal("(Vip mix) gob", TextTransform.ToSentence("(VIP MIX) GOB"));
        Assert.Equal("12 Monkeys", TextTransform.ToSentence("12 MONKEYS"));
    }

    /// <summary>No hunting for full stops: "feat." and "Vol. 2" would be found long before a
    /// sentence, and a tag field is not prose.</summary>
    [Fact]
    public void Sentence_does_not_capitalise_after_a_dot()
    {
        Assert.Equal("Perc feat. ansome", TextTransform.ToSentence("PERC FEAT. ANSOME"));
    }

    // -- Tidy ------------------------------------------------------------------------------------

    [Fact]
    public void Tidy_collapses_runs_and_trims_the_ends()
    {
        Assert.Equal("Perc - Gob", TextTransform.Tidy("   Perc  -   Gob  "));
    }

    /// <summary>A tab, a line break and a non-breaking space all become a plain space - those are
    /// what a copy-and-paste out of a web page leaves behind, and they are invisible in a field.</summary>
    [Fact]
    public void Tidy_normalises_every_kind_of_whitespace()
    {
        var nbsp = (char)0x00A0;
        Assert.Equal("Perc Gob Live", TextTransform.Tidy($"Perc{nbsp}Gob\tLive"));
    }

    [Fact]
    public void Tidy_leaves_an_already_clean_value_alone()
    {
        Assert.Equal("Perc - Gob", TextTransform.Tidy("Perc - Gob"));
    }

    [Fact]
    public void Tidy_of_only_whitespace_is_empty()
    {
        Assert.Equal("", TextTransform.Tidy("   "));
    }

    // -- Apply, and the empty cases --------------------------------------------------------------

    [Theory]
    [InlineData(TextOperation.Lowercase)]
    [InlineData(TextOperation.Uppercase)]
    [InlineData(TextOperation.TitleCase)]
    [InlineData(TextOperation.SentenceCase)]
    [InlineData(TextOperation.Tidy)]
    [InlineData(TextOperation.Replace)]
    public void Null_in_null_out(TextOperation op)
    {
        // A field with nothing in it has nothing to transform, and must NOT come back as an empty
        // string - "" is a value, and writing it would read as "clear this field".
        Assert.Null(TextTransform.Apply(op, null, find: "a", with: "b"));
    }

    [Fact]
    public void Apply_routes_to_the_named_operation()
    {
        Assert.Equal("perc", TextTransform.Apply(TextOperation.Lowercase, "PERC"));
        Assert.Equal("PERC", TextTransform.Apply(TextOperation.Uppercase, "perc"));
        Assert.Equal("Perc Gob", TextTransform.Apply(TextOperation.TitleCase, "PERC GOB"));
        Assert.Equal("Perc gob", TextTransform.Apply(TextOperation.SentenceCase, "PERC GOB"));
        Assert.Equal("Perc Gob", TextTransform.Apply(TextOperation.Tidy, "Perc   Gob "));
        Assert.Equal("Perc-Gob",
                     TextTransform.Apply(TextOperation.Replace, "Perc_Gob", find: "_", with: "-"));
    }

    /// <summary>Only Replace can be half-filled, and an empty "find" is not an instruction - the UI
    /// hangs its Apply button off this so a blank box cannot report "nothing changes" as an answer.</summary>
    [Fact]
    public void Only_replace_needs_something_to_find()
    {
        Assert.False(TextTransform.IsUsable(TextOperation.Replace, ""));
        Assert.False(TextTransform.IsUsable(TextOperation.Replace, null));
        Assert.True(TextTransform.IsUsable(TextOperation.Replace, "_"));
        Assert.True(TextTransform.IsUsable(TextOperation.TitleCase, null));
        Assert.True(TextTransform.IsUsable(TextOperation.Tidy, null));
    }
}
