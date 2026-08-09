using System;
using System.IO;

namespace JustPlay.Core.Organise;

/// <summary>
/// The read-only questions <see cref="OrganisePlanner"/> asks about the disk. Injected so the
/// planner is PURE - every decision it makes ("this one collides", "this one has to cross a
/// volume", "this folder cannot be written to") is testable without a single real file.
///
/// <para>The same idiom the rest of the repo uses for its decision cores, one step up: where
/// <c>WatchQueue</c> and <c>AnalysisQueue</c> only needed a <c>Func&lt;DateTimeOffset&gt;</c>, a file
/// plan needs five answers, and five delegates in a constructor is a worse interface than an
/// interface.</para>
///
/// <para>Nothing here writes, moves, deletes or opens a file - an implementation that did would make
/// "preview before anything happens" a lie.</para>
/// </summary>
public interface IFileProbe
{
    /// <summary>Is there a FILE at this path right now?</summary>
    bool FileExists(string path);

    /// <summary>Is there a FOLDER at this path right now?</summary>
    bool DirectoryExists(string path);

    /// <summary>Size in bytes, or 0 when it cannot be read. Never throws.</summary>
    long SizeOf(string path);

    /// <summary>
    /// Which volume a path lives on, as an opaque comparable token - a drive root on Windows, a
    /// share root for UNC, the mount point elsewhere. Null when it cannot be worked out; two nulls
    /// are treated as "cannot tell", never as "the same volume".
    /// </summary>
    string? VolumeOf(string path);

    /// <summary>Can we actually put a file into this folder? Never throws.</summary>
    bool CanWriteTo(string folder);
}

/// <summary>
/// The real disk. Thin on purpose: every method is one BCL call plus a swallowed exception, because
/// a share that just went away is an ordinary state for a music library on a NAS and must read as
/// "no" rather than take a window down (the suite's never-crash rule).
/// </summary>
public sealed class SystemFileProbe : IFileProbe
{
    /// <summary>One instance is enough - it holds nothing.</summary>
    public static readonly SystemFileProbe Instance = new();

    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public long SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0L;
        }
        catch (Exception)
        {
            return 0L;
        }
    }

    public string? VolumeOf(string path) => VolumeKey(path);

    /// <summary>
    /// The volume token, shared with the runner so the preview and the execution can never disagree
    /// about whether a move is a rename or a copy.
    ///
    /// <para><c>Path.GetPathRoot</c> gives "C:\" for a local path and "\\server\share" for UNC,
    /// which is exactly the granularity that decides whether <c>File.Move</c> is a rename: two
    /// shares on the same NAS are different volumes as far as the filesystem is concerned.</para>
    /// </summary>
    public static string? VolumeKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : root.TrimEnd('\\', '/').ToUpperInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a folder will take a new file. Asked by actually creating and removing a zero-byte
    /// probe file rather than by reading an ACL: a read-only share, a full disk and a folder someone
    /// else has locked all present differently in the permission bits, and the only question that
    /// matters is whether the write lands.
    /// </summary>
    public bool CanWriteTo(string folder)
    {
        if (!DirectoryExists(folder)) return false;

        var probe = Path.Combine(folder, ".just-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                                               FileShare.None, bufferSize: 1,
                                               FileOptions.DeleteOnClose))
            {
                // DeleteOnClose takes the probe with it, so a crash between here and the dispose
                // cannot leave litter in her library.
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // Belt and braces for the platforms where DeleteOnClose is advisory.
            try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception) { }
        }
    }
}
