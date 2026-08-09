using System;
using System.IO;
using System.Runtime.CompilerServices;
using JustPlay.Core.Storage;

namespace JustPlay.Library.Tests;

/// <summary>
/// Relocates the whole suite data tree for this test process, into a temp folder of its own.
///
/// <para>(!!) THIS EXISTS BECAUSE THE TESTS WERE WRITING INTO THE REAL MACHINE'S DATA FOLDER.
/// Measured 2026-08-09: <c>%LOCALAPPDATA%\JUST\library</c> held <b>223</b> stray
/// <c>jp-orgsync-tests-*.db</c> files, and one run added <b>ten more</b> - one per test that opened a
/// database for its temp root. That folder is where the user's real library index lives, next to
/// theirs. A test suite has no business there at all.</para>
///
/// <para>Deleting them afterwards was already being attempted and does not work: SQLite's connection
/// pool still holds the file when the test disposes, <c>File.Delete</c> throws, and the catch that
/// swallows it turns the failure into silence. The answer is not a better cleanup - it is to never
/// write there, so there is nothing to clean up.</para>
///
/// <para><see cref="JustDataPaths"/> already offers exactly this seam for exactly this reason, and
/// resolves it ONCE in a static constructor. A module initializer is therefore the only place late
/// enough to be automatic and early enough to be in time: it runs before any other code in this
/// assembly, so nothing can have read the base path yet.</para>
/// </summary>
internal static class TestDataRoot
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var dir = Path.Combine(Path.GetTempPath(),
                               "jp-testdata-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(JustDataPaths.OverrideVariable, dir);

        // Best effort, and deliberately not more than that: the point is that the files are in TEMP
        // rather than in the user's data folder, which is somewhere the OS is allowed to clear and
        // nobody has to think about. A handle the pool still holds must not fail the run.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
}
