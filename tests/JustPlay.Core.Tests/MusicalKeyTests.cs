using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

public class MusicalKeyTests
{
    [Theory]
    // Anchor points of the Camelot wheel.
    [InlineData(9, KeyMode.Minor, "A minor", "8A")]
    [InlineData(0, KeyMode.Major, "C major", "8B")]
    [InlineData(7, KeyMode.Major, "G major", "9B")]
    [InlineData(4, KeyMode.Minor, "E minor", "9A")]
    [InlineData(6, KeyMode.Major, "F# major", "2B")]
    [InlineData(8, KeyMode.Minor, "G# minor", "1A")]
    public void MapsToExpectedNameAndCamelot(int pitchClass, KeyMode mode, string name, string camelot)
    {
        var key = new MusicalKey(pitchClass, mode);
        Assert.Equal(name, key.Name);
        Assert.Equal(camelot, key.Camelot);
    }

    [Fact]
    public void PitchClassWrapsAround()
    {
        Assert.Equal("C major", new MusicalKey(12, KeyMode.Major).Name);
    }

    [Theory]
    // Camelot
    [InlineData("8A", 9, KeyMode.Minor)]   // A minor
    [InlineData("8B", 0, KeyMode.Major)]   // C major
    [InlineData("12B", 4, KeyMode.Major)]  // E major (two-digit Camelot)
    [InlineData("1a", 8, KeyMode.Minor)]   // G# minor (lowercase)
    // Musical
    [InlineData("Am", 9, KeyMode.Minor)]
    [InlineData("A minor", 9, KeyMode.Minor)]
    [InlineData("C", 0, KeyMode.Major)]
    [InlineData("F#m", 6, KeyMode.Minor)]
    [InlineData("Bbm", 10, KeyMode.Minor)] // A#/Bb minor
    [InlineData("Abmaj", 8, KeyMode.Major)]// G#/Ab major
    public void TryParse_UnderstandsCamelotAndMusical(string text, int pitchClass, KeyMode mode)
    {
        var key = MusicalKey.TryParse(text);
        Assert.Equal(new MusicalKey(pitchClass, mode), key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("13A")]   // out of range
    [InlineData("8C")]    // bad letter
    [InlineData("xyz")]
    public void TryParse_ReturnsNull_ForJunk(string? text)
    {
        Assert.Null(MusicalKey.TryParse(text));
    }
}
