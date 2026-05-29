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
}
