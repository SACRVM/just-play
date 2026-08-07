using System;
using System.IO;
using JustPlay.Core.Storage;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// The rules behind <c>JUSTPLAY_DATA_DIR</c>. They are exercised through the pure
/// <see cref="JustDataPaths.Resolve"/> rather than the real environment on purpose: the public
/// <see cref="JustDataPaths.Base"/> is resolved ONCE per process, so a test that set the variable
/// would either come too late or leak into every other test in the run.
/// </summary>
public class JustDataPathsTests
{
    private const string Fallback = @"C:\Users\somebody\AppData\Local";

    [Fact]
    public void Unset_falls_back_to_the_platform_folder()
    {
        Assert.Equal(Fallback, JustDataPaths.Resolve(null, Fallback));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_counts_as_unset(string raw)
    {
        // An exported-but-empty variable is a shell accident, not a request to write to "".
        Assert.Equal(Fallback, JustDataPaths.Resolve(raw, Fallback));
    }

    [Fact]
    public void A_set_path_wins()
    {
        var target = Path.Combine(Path.GetTempPath(), "just-fresh");
        Assert.Equal(Path.GetFullPath(target), JustDataPaths.Resolve(target, Fallback));
    }

    [Fact]
    public void Surrounding_quotes_and_spaces_are_stripped()
    {
        var target = Path.Combine(Path.GetTempPath(), "just fresh");
        Assert.Equal(Path.GetFullPath(target), JustDataPaths.Resolve($"  \"{target}\"  ", Fallback));
    }

    [Fact]
    public void A_relative_value_becomes_absolute()
    {
        var resolved = JustDataPaths.Resolve("just-fresh", Fallback);

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(Path.GetFullPath("just-fresh"), resolved);
    }

    [Fact]
    public void A_value_the_platform_rejects_falls_back_instead_of_throwing()
    {
        // A typo'd variable must not stop the app from starting - losing the redirection is
        // recoverable, refusing to launch is not.
        Assert.Equal(Fallback, JustDataPaths.Resolve("C:\\bad\0path", Fallback));
    }

    [Fact]
    public void Combine_hangs_segments_off_the_base()
    {
        Assert.Equal(
            Path.Combine(JustDataPaths.Base, "JustPlay", "logs"),
            JustDataPaths.Combine("JustPlay", "logs"));
    }

    [Fact]
    public void Resolving_twice_changes_nothing()
    {
        // Guards the shape the callers rely on: whatever comes out is a stable absolute path, so
        // "<base>\JustPlay\settings.json" means the same thing on every read.
        var once  = JustDataPaths.Resolve(Path.Combine(Path.GetTempPath(), "just-fresh"), Fallback);
        var twice = JustDataPaths.Resolve(once, Fallback);

        Assert.Equal(once, twice);
    }
}
