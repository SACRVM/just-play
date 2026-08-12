using JustPlay.Core.Tagging;

namespace JustPlay.Core.Tests;

/// <summary>
/// The TOLERANT pass of <see cref="FileNameMask.Parse"/>, and the line it must not cross.
///
/// <para>Parsing is the direction that GUESSES, and the guess is written into files. The class states
/// its own rule: a name that does not fit the mask comes back null and the file is left alone. The
/// tolerant pass exists so one naming scheme written by two tools still matches - not so that any
/// name matches any mask.</para>
///
/// <para>Where it crossed the line: every literal run became "[\s_]*", zero-or-more. For a mask whose
/// separator is ONLY whitespace that removes the separator entirely, and "%artist% %title%" started
/// matching names with no space in them at all - splitting on the first character.</para>
/// </summary>
public class FileNameMaskToleranceTests
{
    private const string ArtistTitle = "%artist% - %title%";
    private const string SpaceOnly = "%artist% %title%";

    private static string? Field(string mask, string name, string field) =>
        FileNameMask.Parse(mask, name) is { } r && r.TryGetValue(field, out var v) ? v : null;

    /// <summary>The bug, from the direction it hurts: a one-character artist invented out of a name
    /// that simply does not fit.</summary>
    [Theory]
    [InlineData("SomeTrack")]
    [InlineData("Untitled")]
    [InlineData("Bulk03Cinder")]
    public void A_name_with_no_separator_does_not_fit_a_separator_mask(string name)
    {
        Assert.Null(FileNameMask.Parse(SpaceOnly, name));
    }

    /// <summary>And the reason the tolerant pass exists in the first place, still working: the
    /// separator is a DASH, so the spaces around it stay optional.</summary>
    [Fact]
    public void A_dash_still_anchors_the_split_without_its_spaces()
    {
        Assert.Equal("Perc", Field(ArtistTitle, "Perc-Gob", "artist"));
        Assert.Equal("Gob", Field(ArtistTitle, "Perc-Gob", "title"));
    }

    /// <summary>An underscore where the mask has a space - the other half of the tolerance.</summary>
    [Fact]
    public void An_underscore_still_stands_in_for_a_space()
    {
        Assert.Equal("Perc", Field(SpaceOnly, "Perc_Gob", "artist"));
        Assert.Equal("Gob", Field(SpaceOnly, "Perc_Gob", "title"));
    }

    /// <summary>The strict pass keeps first refusal, so a dash INSIDE a field is not mistaken for the
    /// separator - the case the two-pass design was built for.</summary>
    [Fact]
    public void The_strict_pass_still_wins_where_it_matches()
    {
        Assert.Equal("AC-DC", Field(ArtistTitle, "AC-DC - Highway", "artist"));
        Assert.Equal("Highway", Field(ArtistTitle, "AC-DC - Highway", "title"));
    }

    /// <summary>A plain, well-formed name is unaffected either way.</summary>
    [Fact]
    public void An_ordinary_name_parses_as_it_always_did()
    {
        Assert.Equal("Perc", Field(SpaceOnly, "Perc Gob", "artist"));
        Assert.Equal("Gob", Field(SpaceOnly, "Perc Gob", "title"));
    }
}
