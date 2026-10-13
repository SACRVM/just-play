using Xunit;

namespace JustPlay.Tests;

/// <summary>
/// What lets the suite's Windows-written tests run on another OS. Linked into each test project
/// that needs it (see its csproj), so there is one definition rather than one copy per project.
///
/// <para>Most of these tests were written on Windows and spell their paths that way. On Linux a
/// backslash is an ordinary filename character and "C:\music" is not rooted, so such a path quietly
/// becomes ONE relative name below the working directory - and the test fails for a reason that has
/// nothing to do with what it pins. <see cref="Local"/> keeps the spelling the test was written in
/// and hands the code under test the same path in the shape of the OS running it.</para>
/// </summary>
internal static class TestPath
{
    /// <summary>
    /// A Windows-spelled test path in the local shape. On Windows it comes back untouched, so the
    /// tests there run exactly as written. Elsewhere the segments stay and the separators become '/':
    /// "C:\music\a.mp3" -> "/C/music/a.mp3", "\\nas\music" -> "/nas/music", and a relative
    /// "GENRES\a.mp3" -> "GENRES/a.mp3".
    /// </summary>
    public static string Local(string windowsPath)
    {
        if (OperatingSystem.IsWindows()) return windowsPath;

        var path = windowsPath.Replace('\\', '/');
        if (path.StartsWith("//", StringComparison.Ordinal)) return path[1..];       // a share
        if (path.Length >= 2 && path[1] == ':') return "/" + path[0] + path[2..];   // a drive
        return path;
    }
}

/// <summary>
/// A [Fact] about something only Windows has - a drive-relative path, an ACL, a sharing lock that
/// refuses a rename. Reported as Skipped elsewhere, not as Passed: xunit 2.9.x has no dynamic
/// <c>Assert.Skip</c>, so the skip is set from the constructor, the same way
/// <c>RawTagReaderTests.RequiresCorpusFactAttribute</c> does it (see that file for why it works).
/// </summary>
internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    /// <param name="why">What Windows has that the other OS does not - shown as the skip reason.</param>
    public WindowsOnlyFactAttribute(string why)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only: " + why;
    }
}

/// <summary>The <see cref="WindowsOnlyFactAttribute"/> of a [Theory]: every row is skipped.</summary>
internal sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    /// <param name="why">What Windows has that the other OS does not - shown as the skip reason.</param>
    public WindowsOnlyTheoryAttribute(string why)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only: " + why;
    }
}

/// <summary>A [Fact] about something only Linux has - /proc, a mount table read from it. Skipped elsewhere.</summary>
internal sealed class LinuxOnlyFactAttribute : FactAttribute
{
    /// <param name="why">What Linux has that the other OS does not - shown as the skip reason.</param>
    public LinuxOnlyFactAttribute(string why)
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only: " + why;
    }
}
