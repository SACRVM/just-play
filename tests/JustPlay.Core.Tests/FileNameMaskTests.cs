using System.Linq;
using JustPlay.Core.Tagging;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// The mask language, both directions. Parsing is the half that GUESSES - and the guess is written
/// into however many files were selected - so the cases that decide where a name splits are pinned
/// here rather than checked by eye once.
/// </summary>
public class FileNameMaskTests
{
    // -- Parsing: the ordinary shapes ------------------------------------------------------------

    [Fact]
    public void Parses_artist_and_title()
    {
        var r = FileNameMask.Parse("%artist% - %title%", "Rebekah - Mind Control");

        Assert.NotNull(r);
        Assert.Equal("Rebekah", r!["artist"]);
        Assert.Equal("Mind Control", r["title"]);
    }

    /// <summary>
    /// THE case that decides the whole design: the separator between two fields is usually also
    /// inside the second one. "A - B - C" has to be artist "A" / title "B - C", because a track
    /// called "Mind Control - Remix" is ordinary and an artist called "Rebekah - Mind" is not.
    /// </summary>
    [Fact]
    public void Splits_at_the_FIRST_separator_leaving_the_rest_to_the_last_field()
    {
        var r = FileNameMask.Parse("%artist% - %title%", "Rebekah - Mind Control - Remix");

        Assert.Equal("Rebekah", r!["artist"]);
        Assert.Equal("Mind Control - Remix", r["title"]);
    }

    [Fact]
    public void Parses_a_three_field_mask()
    {
        var r = FileNameMask.Parse("%track% - %artist% - %title%", "03 - Perc - Look What Your Love");

        Assert.Equal("03", r!["track"]);
        Assert.Equal("Perc", r["artist"]);
        Assert.Equal("Look What Your Love", r["title"]);
    }

    [Fact]
    public void Dummy_matches_and_is_thrown_away()
    {
        var r = FileNameMask.Parse("%dummy% - %artist% - %title%", "01 - Perc - Gob");

        Assert.False(r!.ContainsKey("dummy"));
        Assert.Equal("Perc", r["artist"]);
        Assert.Equal("Gob", r["title"]);
    }

    [Fact]
    public void Brackets_and_other_literals_are_matched_literally()
    {
        var r = FileNameMask.Parse("%artist% - %title% (%album%)",
                                   "Ansome - Stowaway (Bulk EP)");

        Assert.Equal("Ansome", r!["artist"]);
        Assert.Equal("Stowaway", r["title"]);
        Assert.Equal("Bulk EP", r["album"]);
    }

    /// <summary>A regex metacharacter in the mask is a LITERAL - masks are not patterns.</summary>
    [Fact]
    public void Regex_characters_in_the_mask_are_literal()
    {
        var r = FileNameMask.Parse("%artist% [%title%]", "Ancient Methods [The Jericho Records]");

        Assert.Equal("Ancient Methods", r!["artist"]);
        Assert.Equal("The Jericho Records", r["title"]);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var r = FileNameMask.Parse("%artist%-%title%", "  Perc  -  Gob  ");

        Assert.Equal("Perc", r!["artist"]);
        Assert.Equal("Gob", r["title"]);
    }

    // -- Parsing: the cases that must REFUSE -----------------------------------------------------

    /// <summary>
    /// A name that does not fit must come back null, not half-filled. This is the whole safety of
    /// the feature: the file is then left alone instead of being given whatever a loose pattern
    /// happened to capture.
    /// </summary>
    [Fact]
    public void Returns_null_when_the_name_does_not_fit()
    {
        Assert.Null(FileNameMask.Parse("%artist% - %title%", "no separator here"));
    }

    [Fact]
    public void Returns_null_for_a_mask_with_no_placeholders()
    {
        Assert.Null(FileNameMask.Parse("just some text", "just some text"));
        Assert.False(FileNameMask.CanParse("just some text"));
    }

    /// <summary>A typo like %titel% must refuse the whole mask. Treating it as a literal would make
    /// it match nothing while looking like it should work.</summary>
    [Fact]
    public void Returns_null_for_an_unknown_placeholder()
    {
        Assert.Null(FileNameMask.Parse("%artist% - %titel%", "Perc - Gob"));
        Assert.False(FileNameMask.CanParse("%artist% - %titel%"));
    }

    /// <summary>The same field twice would be two different answers for one value.</summary>
    [Fact]
    public void Returns_null_when_a_field_appears_twice()
    {
        Assert.Null(FileNameMask.Parse("%artist% - %artist%", "Perc - Perc"));
    }

    [Fact]
    public void An_empty_capture_is_left_out_rather_than_written_as_blank()
    {
        var r = FileNameMask.Parse("%artist% - %title%", "Perc -  ");
        Assert.Null(r);   // ".+" needs at least one character, so this simply does not fit
    }

    // -- Tolerance: one scheme, written by four different tools -----------------------------------
    //
    // The strict pattern gets first refusal; only a name that does not fit it AT ALL is offered the
    // loose one. So these all parse with the SAME mask, and the precise cases below still stay
    // precise.

    [Theory]
    [InlineData("Perc - Gob")]                 // as written
    [InlineData("Perc_-_Gob")]                 // underscores, the web-download shape
    [InlineData("Perc-Gob")]                   // no spaces at all
    [InlineData("Perc \u2013 Gob")]            // EN dash
    [InlineData("Perc \u2014 Gob")]            // EM dash
    public void One_mask_covers_the_ways_the_same_scheme_gets_written(string name)
    {
        var r = FileNameMask.Parse("%artist% - %title%", name);

        Assert.NotNull(r);
        Assert.Equal("Perc", r!["artist"]);
        Assert.Equal("Gob", r["title"]);
    }

    /// <summary>
    /// THE reason the strict pass runs first. With the tolerant pattern alone, "AC-DC - Highway"
    /// splits at the hyphen inside the band's name and yields artist "AC" - a case the exact
    /// pattern gets right, broken by the leniency meant to help.
    /// </summary>
    [Fact]
    public void A_dash_inside_a_name_is_not_eaten_when_the_strict_form_already_fits()
    {
        var r = FileNameMask.Parse("%artist% - %title%", "AC-DC - Highway To Hell");

        Assert.Equal("AC-DC", r!["artist"]);
        Assert.Equal("Highway To Hell", r["title"]);
    }

    /// <summary>
    /// Where the tolerance STOPS, and why it has to. A mask that names a dash requires a dash: make
    /// it optional and "%artist% - %title%" would also swallow "Hard Techno Mix" as artist "Hard".
    /// The separator is the instruction; only its spelling is negotiable. For underscore-only names
    /// the mask to type is "%artist%_%title%".
    /// </summary>
    [Fact]
    public void A_mask_that_names_a_dash_needs_a_dash()
    {
        Assert.Null(FileNameMask.Parse("%artist% - %title%", "Perc_Gob"));

        var r = FileNameMask.Parse("%artist%_%title%", "Perc_Gob");
        Assert.Equal("Perc", r!["artist"]);
        Assert.Equal("Gob", r["title"]);
    }

    /// <summary>Brackets, dots and words in the mask stay EXACT even in the tolerant pass - those
    /// are the scheme, not the way it happens to be typed.</summary>
    [Fact]
    public void Tolerance_does_not_loosen_brackets()
    {
        Assert.Null(FileNameMask.Parse("%artist% - %title% (%album%)", "Perc - Gob [Bulk]"));
    }

    // -- The folder is data too -------------------------------------------------------------------

    [Fact]
    public void A_mask_can_reach_into_the_folder()
    {
        const string mask = "%genre%/%artist% - %title%";
        var subject = FileNameMask.SubjectFor(mask, @"\\nas\music\GENRES\Hard Techno\Perc - Gob.mp3");

        Assert.Equal("Hard Techno/Perc - Gob", subject);

        var r = FileNameMask.Parse(mask, subject);
        Assert.Equal("Hard Techno", r!["genre"]);
        Assert.Equal("Perc", r["artist"]);
        Assert.Equal("Gob", r["title"]);
    }

    [Fact]
    public void A_mask_may_be_typed_with_either_slash()
    {
        var subject = FileNameMask.SubjectFor(@"%album%\%title%", @"C:\music\Bulk EP\Stowaway.flac");
        var r = FileNameMask.Parse(@"%album%\%title%", subject);

        Assert.Equal("Bulk EP", r!["album"]);
        Assert.Equal("Stowaway", r["title"]);
    }

    [Fact]
    public void A_plain_mask_only_ever_sees_the_file_name()
    {
        Assert.Equal(1, FileNameMask.SegmentCount("%artist% - %title%"));
        Assert.Equal("Perc - Gob",
                     FileNameMask.SubjectFor("%artist% - %title%", @"C:\music\Techno\Perc - Gob.mp3"));
    }

    [Fact]
    public void SegmentCount_counts_the_folders_a_mask_reaches_over()
    {
        Assert.Equal(3, FileNameMask.SegmentCount("%genre%/%album%/%title%"));
    }

    // -- The other direction ---------------------------------------------------------------------

    [Fact]
    public void Format_fills_the_placeholders()
    {
        var name = FileNameMask.Format("%artist% - %title%",
                                       f => f switch { "artist" => "Perc", "title" => "Gob", _ => null });

        Assert.Equal("Perc - Gob", name);
    }

    [Fact]
    public void Format_collapses_a_placeholder_with_no_value()
    {
        var name = FileNameMask.Format("%artist% - %title% (%album%)",
                                       f => f switch { "artist" => "Perc", "title" => "Gob", _ => null });

        Assert.Equal("Perc - Gob ()", name);   // the caller tidies the leftovers
    }

    [Fact]
    public void Format_drops_dummy()
    {
        Assert.Equal(" - Gob", FileNameMask.Format("%dummy% - %title%",
                                                   f => f == "title" ? "Gob" : null));
    }

    // -- Which fields a mask touches -------------------------------------------------------------

    [Fact]
    public void FieldsIn_lists_them_in_order_without_dummy()
    {
        Assert.Equal(["track", "artist", "title"],
                     FileNameMask.FieldsIn("%track% - %dummy% - %artist% - %title%").ToArray());
    }

    [Fact]
    public void FieldsIn_is_empty_for_nothing_useful()
    {
        Assert.Empty(FileNameMask.FieldsIn(null));
        Assert.Empty(FileNameMask.FieldsIn(""));
        Assert.Empty(FileNameMask.FieldsIn("no placeholders"));
    }
}
