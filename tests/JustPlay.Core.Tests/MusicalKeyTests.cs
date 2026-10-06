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

    /// <summary>
    /// The real music signs (U+266F sharp, U+266D flat) parse exactly like '#' and 'b'. They were
    /// lost once when the source went ASCII-only: the literal sign became a second '#' / 'b', and
    /// "F\u266Fm" quietly stopped parsing. Every form is pinned so that cannot recur unnoticed.
    /// </summary>
    [Theory]
    [InlineData("F\u266Fm", 6, KeyMode.Minor)]        // sharp, short minor
    [InlineData("F\u266F minor", 6, KeyMode.Minor)]   // sharp, long minor
    [InlineData("F\u266F", 6, KeyMode.Major)]         // sharp, bare major
    [InlineData("C\u266F major", 1, KeyMode.Major)]   // sharp, long major
    [InlineData("C\u266Fmaj", 1, KeyMode.Major)]      // sharp, short major
    [InlineData("B\u266Dm", 10, KeyMode.Minor)]       // flat, short minor
    [InlineData("B\u266D minor", 10, KeyMode.Minor)]  // flat, long minor
    [InlineData("E\u266D", 3, KeyMode.Major)]         // flat, bare major
    [InlineData("A\u266D major", 8, KeyMode.Major)]   // flat, long major
    [InlineData("D\u266Dmaj", 1, KeyMode.Major)]      // flat, short major
    [InlineData("C\u266D", 11, KeyMode.Major)]        // flat wraps below C to B
    public void TryParse_UnderstandsTheMusicSigns(string text, int pitchClass, KeyMode mode)
    {
        Assert.Equal(new MusicalKey(pitchClass, mode), MusicalKey.TryParse(text));
    }

    /// <summary>The sign and its ASCII stand-in name the same key.</summary>
    [Theory]
    [InlineData("F\u266Fm", "F#m")]
    [InlineData("B\u266D minor", "Bb minor")]
    [InlineData("A\u266Dmaj", "Abmaj")]
    public void TryParse_TheSignAndItsAsciiFormAgree(string sign, string ascii)
    {
        Assert.NotNull(MusicalKey.TryParse(sign));
        Assert.Equal(MusicalKey.TryParse(ascii), MusicalKey.TryParse(sign));
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
