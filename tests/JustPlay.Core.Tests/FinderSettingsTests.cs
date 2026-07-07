using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

public class FinderSettingsTests
{
    [Fact]
    public void Defaults_NoRoot_Step30()
    {
        var s = FinderSettings.Defaults;

        Assert.Null(s.LibraryRoot);
        Assert.Equal(30, s.SeekStepSeconds);
    }

    [Fact]
    public void RoundTrips_AllFields()
    {
        var s = new FinderSettings
        {
            LibraryRoot = @"\\nas\music\GENRES",
            SeekStepSeconds = 15,
        };

        var restored = FinderSettingsCodec.TryParse(FinderSettingsCodec.Serialize(s));

        Assert.Equal(@"\\nas\music\GENRES", restored.LibraryRoot);
        Assert.Equal(15, restored.SeekStepSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void TryParse_AbsentOrCorrupt_FallsBackToDefaults(string? json)
    {
        Assert.Equal(FinderSettings.Defaults, FinderSettingsCodec.TryParse(json));
    }

    [Fact]
    public void TryParse_JsonNullLiteral_FallsBackToDefaults()
    {
        Assert.Equal(FinderSettings.Defaults, FinderSettingsCodec.TryParse("null"));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    public void Normalized_AllowedSteps_Unchanged(int step)
    {
        var s = new FinderSettings { SeekStepSeconds = step };

        Assert.Equal(step, s.Normalized().SeekStepSeconds);
    }

    [Theory]
    [InlineData(60)]   // the explicitly rejected step
    [InlineData(0)]
    [InlineData(-5)]
    public void Normalized_UnknownStep_SnapsTo30(int step)
    {
        var s = new FinderSettings { SeekStepSeconds = step };

        Assert.Equal(30, s.Normalized().SeekStepSeconds);
    }

    [Fact]
    public void TryParse_HandEditedStep_IsSnapped()
    {
        // A hand-edited file with a step the UI never offers must still light a radio.
        var restored = FinderSettingsCodec.TryParse("""{ "SeekStepSeconds": 60 }""");

        Assert.Equal(30, restored.SeekStepSeconds);
    }

    [Fact]
    public void TryParse_IsCaseInsensitive_ForHandEdits()
    {
        var restored = FinderSettingsCodec.TryParse(
            """{ "libraryroot": "D:\\music", "seekstepseconds": 20 }""");

        Assert.Equal(@"D:\music", restored.LibraryRoot);
        Assert.Equal(20, restored.SeekStepSeconds);
    }
}
