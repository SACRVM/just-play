using JustPlay.Core.Models;

namespace JustPlay.Tag.Tests;

/// <summary>
/// What JUST TAG's search actually decides. Written because Chloe reported it behaving "inverted"
/// (2026-08-05) and the logic was private inside the view model, i.e. only checkable by clicking -
/// which is how a filter can be wrong for a while without anyone being able to say how.
///
/// <para>Every mode is pinned twice: once on a row that HAS the field, once on a row where the field is
/// ABSENT. The absent case is the one that gets written backwards, and it is the case a tagger lives in.</para>
/// </summary>
public sealed class TagSearchTests
{
    private static FileRow Row(string name = "01 - Some Track.mp3", TrackMetadata? meta = null,
                               string? id3 = null)
    {
        var row = new FileRow(name, @"C:\music\" + name) { Meta = meta, Id3 = id3 };
        return row;
    }

    private static TrackMetadata Meta(string? genre = null, string? artist = null, string? title = null,
                                      byte[]? cover = null) => new()
    {
        FallbackName = "Some Track",
        Genre = genre,
        Artist = artist,
        Title = title,
        CoverArt = cover,
    };

    private static bool Match(FileRow row, TagField f, MatchMode? m, string? v) =>
        TagSearch.Matches(row, f, m, v);

    // -- The positive modes ----------------------------------------------------------------------

    [Theory]
    [InlineData(MatchMode.Contains,   "tech", true)]
    [InlineData(MatchMode.Contains,   "TECH", true)]   // case-insensitive
    [InlineData(MatchMode.Contains,   "house", false)]
    [InlineData(MatchMode.Is,         "hard techno", true)]
    [InlineData(MatchMode.Is,         "techno", false)]
    [InlineData(MatchMode.StartsWith, "hard", true)]
    [InlineData(MatchMode.StartsWith, "techno", false)]
    [InlineData(MatchMode.IsNotEmpty, null, true)]
    [InlineData(MatchMode.IsEmpty,    null, false)]
    public void A_field_that_is_present_compares_the_obvious_way(MatchMode mode, string? value, bool expected) =>
        Assert.Equal(expected, Match(Row(meta: Meta(genre: "hard techno")), TagField.Genre, mode, value));

    [Theory]
    [InlineData(MatchMode.NotContains, "tech", false)]
    [InlineData(MatchMode.NotContains, "house", true)]
    [InlineData(MatchMode.IsNot,       "hard techno", false)]
    [InlineData(MatchMode.IsNot,       "techno", true)]
    public void The_negative_modes_are_the_exact_complement(MatchMode mode, string value, bool expected) =>
        Assert.Equal(expected, Match(Row(meta: Meta(genre: "hard techno")), TagField.Genre, mode, value));

    // -- The absent field - the half that decides whether the filter is useful -------------------

    /// <summary>A file with no genre can never "contain" one. If this inverted, a search for a genre
    /// would return every untagged file in the folder.</summary>
    [Theory]
    [InlineData(MatchMode.Contains)]
    [InlineData(MatchMode.Is)]
    [InlineData(MatchMode.StartsWith)]
    public void An_absent_field_never_satisfies_a_positive_test(MatchMode mode) =>
        Assert.False(Match(Row(meta: Meta()), TagField.Genre, mode, "techno"));

    /// <summary>...and it DOES satisfy a negative one: "genre is not techno" has to return the files with
    /// no genre at all, because those are precisely the ones that need fixing.</summary>
    [Theory]
    [InlineData(MatchMode.NotContains)]
    [InlineData(MatchMode.IsNot)]
    public void An_absent_field_does_satisfy_a_negative_test(MatchMode mode) =>
        Assert.True(Match(Row(meta: Meta()), TagField.Genre, mode, "techno"));

    [Fact]
    public void Is_empty_finds_the_untagged_and_only_those()
    {
        Assert.True(Match(Row(meta: Meta()), TagField.Genre, MatchMode.IsEmpty, null));
        Assert.False(Match(Row(meta: Meta(genre: "techno")), TagField.Genre, MatchMode.IsEmpty, null));
    }

    /// <summary>A row whose tags have not been read yet has NO metadata at all - it must behave exactly
    /// like a file with empty fields, never like a match.</summary>
    [Fact]
    public void A_row_that_was_never_read_behaves_like_an_empty_field()
    {
        var unread = Row();   // Meta == null
        Assert.False(Match(unread, TagField.Genre, MatchMode.Contains, "techno"));
        Assert.True(Match(unread, TagField.Genre, MatchMode.IsEmpty, null));
    }

    // -- ART: present or absent, never text ------------------------------------------------------

    [Fact]
    public void Art_is_found_by_is_not_empty_and_missing_art_by_is_empty()
    {
        var withArt = Row(meta: Meta(cover: [1, 2, 3]));
        var without = Row(meta: Meta());

        Assert.True(Match(withArt, TagField.Cover, MatchMode.IsNotEmpty, null));
        Assert.False(Match(without, TagField.Cover, MatchMode.IsNotEmpty, null));

        Assert.True(Match(without, TagField.Cover, MatchMode.IsEmpty, null));
        Assert.False(Match(withArt, TagField.Cover, MatchMode.IsEmpty, null));
    }

    /// <summary>ART takes no typed value, so the value box is hidden for it whatever the mode says.</summary>
    [Fact]
    public void Art_never_asks_for_a_value()
    {
        Assert.False(TagSearch.NeedsValue(TagField.Cover, MatchMode.Contains));
        Assert.False(TagSearch.NeedsValue(TagField.Cover, null));
        Assert.True(TagSearch.NeedsValue(TagField.Genre, MatchMode.Contains));
        Assert.False(TagSearch.NeedsValue(TagField.Genre, MatchMode.IsEmpty));
    }

    // -- "All fields" ----------------------------------------------------------------------------

    [Fact]
    public void All_fields_looks_in_name_title_artist_and_genre()
    {
        var row = Row("01 - Nightcrawler.mp3", Meta(genre: "hard techno", artist: "Perc", title: "Look What"));

        Assert.True(Match(row, TagField.All, null, "night"));      // file name
        Assert.True(Match(row, TagField.All, null, "perc"));       // artist
        Assert.True(Match(row, TagField.All, null, "look"));       // title
        Assert.True(Match(row, TagField.All, null, "techno"));     // genre
        Assert.False(Match(row, TagField.All, null, "amapiano"));
    }

    [Fact]
    public void All_fields_with_does_not_contain_is_the_complement()
    {
        var row = Row("01 - Nightcrawler.mp3", Meta(artist: "Perc"));

        Assert.False(Match(row, TagField.All, MatchMode.NotContains, "perc"));
        Assert.True(Match(row, TagField.All, MatchMode.NotContains, "amapiano"));
    }

    // -- When a condition counts at all ----------------------------------------------------------

    [Fact]
    public void A_text_mode_with_no_text_is_not_an_active_condition()
    {
        Assert.False(TagSearch.IsActive(TagField.Genre, MatchMode.Contains, ""));
        Assert.False(TagSearch.IsActive(TagField.Genre, MatchMode.Contains, "   "));
        Assert.True(TagSearch.IsActive(TagField.Genre, MatchMode.Contains, "techno"));
    }

    [Fact]
    public void An_emptiness_mode_is_active_without_any_text()
    {
        Assert.True(TagSearch.IsActive(TagField.Genre, MatchMode.IsEmpty, null));
        Assert.True(TagSearch.IsActive(TagField.Cover, MatchMode.IsNotEmpty, null));
        // ART is active as soon as it is chosen, because it never needs text.
        Assert.True(TagSearch.IsActive(TagField.Cover, MatchMode.Contains, null));
    }

    // -- Two conditions --------------------------------------------------------------------------

    [Fact]
    public void And_narrows_or_widens()
    {
        var row = Row(meta: Meta(genre: "hard techno", artist: "Perc"));

        // genre contains techno AND artist contains perc -> true
        Assert.True(TagSearch.Matches(row, TagField.Genre, MatchMode.Contains, "techno",
                                      hasSecond: true, joinAnd: true,
                                      TagField.Artist, MatchMode.Contains, "perc"));

        // genre contains techno AND artist contains blawan -> false
        Assert.False(TagSearch.Matches(row, TagField.Genre, MatchMode.Contains, "techno",
                                       hasSecond: true, joinAnd: true,
                                       TagField.Artist, MatchMode.Contains, "blawan"));

        // ...but OR keeps it
        Assert.True(TagSearch.Matches(row, TagField.Genre, MatchMode.Contains, "techno",
                                      hasSecond: true, joinAnd: false,
                                      TagField.Artist, MatchMode.Contains, "blawan"));
    }

    /// <summary>A second condition that is switched OFF, or switched on but still empty, must not
    /// change the result - neither by narrowing it to nothing nor by widening it to everything.</summary>
    [Fact]
    public void An_inactive_second_condition_changes_nothing()
    {
        var row = Row(meta: Meta(genre: "hard techno"));

        Assert.True(TagSearch.Matches(row, TagField.Genre, MatchMode.Contains, "techno",
                                      hasSecond: false, joinAnd: true,
                                      TagField.Artist, MatchMode.Contains, "blawan"));

        Assert.True(TagSearch.Matches(row, TagField.Genre, MatchMode.Contains, "techno",
                                      hasSecond: true, joinAnd: false,
                                      TagField.Artist, MatchMode.Contains, ""));
    }

    // -- The file facts --------------------------------------------------------------------------

    [Fact]
    public void Id3_version_can_be_asked_for_and_asked_against()
    {
        var v23 = Row(id3: "2.3");
        var none = Row();   // FLAC, or an MP3 with no ID3v2 header

        Assert.True(Match(v23, TagField.Id3Version, MatchMode.Is, "2.3"));
        Assert.True(Match(v23, TagField.Id3Version, MatchMode.IsNot, "2.4"));

        // "everything that is not 2.4" must include the files carrying no ID3 tag at all.
        Assert.True(Match(none, TagField.Id3Version, MatchMode.IsNot, "2.4"));
        Assert.True(Match(none, TagField.Id3Version, MatchMode.IsEmpty, null));
    }

    [Fact]
    public void File_type_comes_from_the_path_and_needs_no_tags()
    {
        var flac = new FileRow("track.flac", @"C:\music\track.flac");
        Assert.True(Match(flac, TagField.FileType, MatchMode.Is, "flac"));
        Assert.False(Match(flac, TagField.FileType, MatchMode.Is, "mp3"));
    }
}
