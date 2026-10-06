using System;
using System.IO;

namespace JustPlay.Core.Organise;

/// <summary>Why a file cannot go to the operating system's bin, or <see cref="None"/> when nothing
/// known stands in the way.</summary>
public enum NoBinReason
{
    /// <summary>Nothing known stands in the way. The shell still has the last word.</summary>
    None,

    /// <summary>A network share - a UNC path, or a drive letter mapped to one.</summary>
    NetworkShare,

    /// <summary>A removable drive - a USB stick, a memory card. Windows keeps no bin for these.</summary>
    RemovableDrive,
}

/// <summary>
/// Can a file reach a recycle bin from where it sits? Decided from the PATH, file by file - never
/// once per machine.
///
/// <para><b>(!!) Why this is asked before the shell is.</b> Windows keeps no Recycle Bin for a
/// network share or a removable drive, and asked to recycle a file there, the shell does not refuse:
/// per the SHFILEOPSTRUCT documentation, FOF_ALLOWUNDO is only honoured where it can be, and a file
/// that cannot be recycled is deleted for good instead - silently, once confirmations are off. So
/// "this machine has a bin" says nothing about the file at hand. Delete means the bin; a file that
/// cannot go there is refused and left where it is.</para>
///
/// <para>Pure string work plus one injected question - the type of a drive, which production answers
/// with <c>GetDriveType</c> through <see cref="DriveInfo"/> - so every path shape is a plain
/// test.</para>
/// </summary>
public static class RecycleBinReach
{
    /// <summary>
    /// The Windows rule: a UNC path is a network share; a drive letter is whatever its drive type says
    /// (<c>DRIVE_REMOTE</c> is a share, <c>DRIVE_REMOVABLE</c> a removable drive). Anything else -
    /// a fixed disk, a volume GUID path, a shape this does not recognise - is <see cref="NoBinReason.None"/>,
    /// and the shell, with its nuke warning on, decides.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <param name="driveTypeOf">The type of a drive root such as "Z:\".</param>
    public static NoBinReason OnWindows(string? path, Func<string, DriveType> driveTypeOf)
    {
        ArgumentNullException.ThrowIfNull(driveTypeOf);

        if (string.IsNullOrWhiteSpace(path)) return NoBinReason.None;
        if (IsUnc(path)) return NoBinReason.NetworkShare;
        if (DriveRoot(path) is not { } root) return NoBinReason.None;

        return driveTypeOf(root) switch
        {
            DriveType.Network   => NoBinReason.NetworkShare,
            DriveType.Removable => NoBinReason.RemovableDrive,
            _                   => NoBinReason.None,
        };
    }

    /// <summary>
    /// True for a path on a server: "\\server\share\...", the same with forward slashes, and the
    /// long form "\\?\UNC\server\share\...". The device prefixes "\\?\" and "\\.\" on their own
    /// are NOT a server - "\\?\C:\..." is a local disk.
    /// </summary>
    public static bool IsUnc(string? path)
    {
        if (path is null || path.Length < 3 || !IsSeparator(path[0]) || !IsSeparator(path[1]))
            return false;

        if (path[2] is '?' or '.' && (path.Length == 3 || IsSeparator(path[3])))
            return path.Length > 8
                && path.AsSpan(4, 3).Equals("UNC", StringComparison.OrdinalIgnoreCase)
                && IsSeparator(path[7]);

        return true;
    }

    /// <summary>The drive root ("Z:\") a drive-letter path sits on - after a device prefix, if it has
    /// one - or null for anything that is not a drive-letter path.</summary>
    public static string? DriveRoot(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var s = path.AsSpan();
        if (s.Length >= 4 && IsSeparator(s[0]) && IsSeparator(s[1]) && s[2] is '?' or '.'
            && IsSeparator(s[3]))
            s = s[4..];

        return s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':'
            ? char.ToUpperInvariant(s[0]) + @":\"
            : null;
    }

    /// <summary>
    /// The per-file failure a refused delete reports, in the words the result list shows. It says
    /// where the file is, that it has no bin, and that it is still there.
    /// </summary>
    /// <param name="reason">Why the file has no bin. <see cref="NoBinReason.None"/> gets a plain
    /// sentence too, so a caller can never show an empty failure.</param>
    /// <param name="binName">What this platform calls its bin - "Recycle Bin", "Trash".</param>
    public static string Describe(NoBinReason reason, string binName)
    {
        var bin = string.IsNullOrWhiteSpace(binName) ? "recycle bin" : binName;

        return reason switch
        {
            NoBinReason.NetworkShare   => $"This file is on a network share, which has no {bin} - it is left where it is.",
            NoBinReason.RemovableDrive => $"This file is on a removable drive, which has no {bin} - it is left where it is.",
            _                          => $"This file cannot go to the {bin} - it is left where it is.",
        };
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
