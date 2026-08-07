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

    // -- Harmonic (Camelot) compatibility ----------------------------------
    // 8A = A minor. Compatible: itself, 7A, 9A (+/-1 same mode), 8B (relative major).

    [Theory]
    [InlineData("8A", "8A")]   // same key
    [InlineData("8A", "7A")]   // -1 hour, same mode
    [InlineData("8A", "9A")]   // +1 hour, same mode
    [InlineData("8A", "8B")]   // relative major/minor
    [InlineData("1A", "12A")]  // wheel wrap 1<->12
    [InlineData("12B", "1B")]  // wheel wrap 12<->1
    public void HarmonicallyCompatible_AdjacentAndRelative(string a, string b)
    {
        var ka = MusicalKey.TryParse(a)!.Value;
        var kb = MusicalKey.TryParse(b)!.Value;
        Assert.True(ka.IsHarmonicallyCompatibleWith(kb));
        Assert.True(kb.IsHarmonicallyCompatibleWith(ka)); // symmetric
    }

    [Theory]
    [InlineData("8A", "10A")]  // 2 hours away, same mode -> clash
    [InlineData("8A", "2A")]   // opposite side of the wheel
    [InlineData("8A", "9B")]   // different number AND different mode
    [InlineData("8A", "7B")]   // diagonal "energy" move - deliberately NOT in the safe set
    public void HarmonicallyIncompatible_Clashes(string a, string b)
    {
        var ka = MusicalKey.TryParse(a)!.Value;
        var kb = MusicalKey.TryParse(b)!.Value;
        Assert.False(ka.IsHarmonicallyCompatibleWith(kb));
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
