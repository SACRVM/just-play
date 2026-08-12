using JustPlay.Library;

namespace JustPlay.Tag.Tests;

/// <summary>
/// The one line a finished analysis run leaves in the FILES header. It is the only part of the
/// report that stays on screen, so what it may NOT do is round a failure away.
/// </summary>
public sealed class AnalysisRunTextTests
{
    [Fact]
    public void CleanRunNamesTheCount()
    {
        var line = AnalysisRunText.Summarise(Report(succeeded: 30));

        Assert.Equal("30 files analysed", line);
    }

    [Fact]
    public void OneFileIsSingular()
    {
        Assert.Equal("1 file analysed", AnalysisRunText.Summarise(Report(succeeded: 1)));
    }

    /// <summary>(!!) 3 of 30 failed has to SAY 3 failed - never leave a song behind, not even in
    /// the summary of what happened to it.</summary>
    [Fact]
    public void FailuresAreAlwaysStated()
    {
        var line = AnalysisRunText.Summarise(Report(succeeded: 27, failed: 3));

        Assert.Equal("27 files analysed - 3 failed", line);
    }

    /// <summary>A stopped run says what is left, so "did it finish?" is answered without counting
    /// rows by hand.</summary>
    [Fact]
    public void StoppedRunSaysWhatIsLeft()
    {
        var line = AnalysisRunText.Summarise(Report(succeeded: 12, remaining: 18, cancelled: true));

        Assert.Equal("12 files analysed - stopped, 18 left", line);
    }

    /// <summary>A run cancelled AFTER the last file had nothing left over - claiming "0 left" would
    /// be noise on a run that in fact completed.</summary>
    [Fact]
    public void CancelledWithNothingLeftReadsAsFinished()
    {
        var line = AnalysisRunText.Summarise(Report(succeeded: 4, cancelled: true));

        Assert.Equal("4 files analysed", line);
    }

    /// <summary>Under a minute the seconds are the useful unit - "0.0 min" answers nothing.</summary>
    [Fact]
    public void ShortRunsAreReportedInSeconds()
    {
        var line = AnalysisRunText.Summarise(
            Report(succeeded: 5, elapsed: TimeSpan.FromSeconds(42)));

        Assert.Equal("5 files analysed in 42 s", line);
    }

    [Fact]
    public void LongRunsAreReportedInMinutes()
    {
        var line = AnalysisRunText.Summarise(
            Report(succeeded: 300, elapsed: TimeSpan.FromMinutes(7.5)));

        Assert.Equal("300 files analysed in 7.5 min", line);
    }

    /// <summary>A sub-second run gets no time at all rather than "0 s".</summary>
    [Fact]
    public void AnInstantRunReportsNoTime()
    {
        var line = AnalysisRunText.Summarise(
            Report(succeeded: 1, elapsed: TimeSpan.FromMilliseconds(120)));

        Assert.Equal("1 file analysed", line);
    }

    private static AnalysisBatchReport Report(
        int succeeded = 0, int failed = 0, int remaining = 0, bool cancelled = false,
        TimeSpan elapsed = default) => new()
    {
        Total     = succeeded + failed + remaining,
        Succeeded = succeeded,
        Failed    = failed,
        Remaining = remaining,
        Cancelled = cancelled,
        Elapsed   = elapsed,
    };
}
