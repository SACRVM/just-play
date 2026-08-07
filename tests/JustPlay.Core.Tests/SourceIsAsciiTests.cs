using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Every file we compile is plain ASCII.
///
/// <para><b>Why this is a test and not a note in a style guide.</b> A source file with no BOM and a
/// non-ASCII byte in it is a file that any tool which GUESSES its encoding can silently destroy - and
/// one did: a find-and-replace run through PowerShell 5.1 read a UTF-8 file as ANSI and turned all
/// 250 typographic characters in it into mojibake. The damage was reversible that time. The next one
/// might not be, and nothing about the file said "handle with care".</para>
///
/// <para>The alternative was to give all 358 files a BOM. It was rejected because a BOM is not
/// self-enforcing: the next file created by a template has none, and the hazard returns unnoticed.
/// ASCII can be checked, so it is checked here, on every build.</para>
///
/// <para>Nothing of value is lost. The characters were section rules, dashes, arrows and maths
/// notation, all of which have plain equivalents that are usually clearer to grep:
/// <c>-</c>, <c>-&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>x</c>, <c>~</c>, <c>+/-</c>, <c>^2</c>,
/// <c>...</c>, <c>(!)</c>, and <c>tau</c> / <c>alpha</c> / <c>pi</c> for the Greek - you cannot type
/// a tau into a search box.</para>
/// </summary>
public class SourceIsAsciiTests
{
    /// <summary>Extensions this covers: everything the build compiles. Docs, skills and reports are
    /// prose read by tools that handle UTF-8 properly, and are deliberately out of scope.</summary>
    private static readonly string[] Extensions = [".cs", ".axaml"];

    [Fact]
    public void No_compiled_source_file_contains_a_non_ascii_character()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            var line = 1;

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') { line++; continue; }
                if (text[i] <= 127) continue;

                offenders.Add(
                    $"{Path.GetRelativePath(RepoRoot, file)}:{line}  U+{(int)text[i]:X4} '{text[i]}'");
                break;   // one report per file is enough to find and fix it
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} source file(s) contain non-ASCII characters. Replace them with the "
            + "plain equivalents (see this test's summary) rather than adding a BOM:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(40)));
    }

    /// <summary>A guard for the guard: if the walk ever finds nothing (a moved test project, a
    /// renamed folder), the test above would pass vacuously and the rule would quietly stop being
    /// enforced.</summary>
    [Fact]
    public void The_scan_actually_reaches_the_source_tree()
    {
        Assert.True(SourceFiles().Count() > 200,
            "The ASCII scan found almost no files - it is no longer looking at the source tree, "
            + "which means the rule is not being enforced at all.");
    }

    private static IEnumerable<string> SourceFiles() =>
        new[] { "src", "tests" }
            .Select(dir => Path.Combine(RepoRoot, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>Walk up from the test binary until the solution file appears - no hard-coded path,
    /// so this keeps working on another machine and in CI.</summary>
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JustPlay.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("JustPlay.slnx not found above the test binary.");
    }
}
