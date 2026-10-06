using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Organise;

namespace JustPlay.UI.Controls;

/// <summary>
/// The operating system's own bin - Windows' Recycle Bin, the Mac's Trash.
///
/// <para>It lives here, beside <see cref="SystemFileBrowser"/>, for the same reason that one does:
/// it is a platform shim with a runtime <c>OperatingSystem.Is...</c> switch, and the layers below
/// this one (Core, Library) may not contain a platform call. The contract it fills is
/// <see cref="IRecycleBin"/> in Core, so the organise engine never sees a Win32 struct.</para>
///
/// <para><b>(!!) This class never falls back to a permanent delete, and it never decides to.</b> On a
/// platform with no bin, <see cref="IsAvailable"/> is false - that is a fact reported upwards, not a
/// veto and not a fallback. The planner turns it into <c>DeleteKind.Permanent</c>, the window states
/// that in the words on the button, and the person in front of it decides. What must never happen is
/// the QUIET version: a delete the user was told would be recoverable that silently was not.</para>
///
/// <para><b>A machine with a bin still has files that cannot reach it</b> - a network share, and on
/// Windows a removable drive. Those are answered per file by <see cref="CanRecycle"/>, so the preview
/// marks them as left alone, and refused by <see cref="Send"/> as a per-file failure. Delete means
/// the bin; such a file stays where it is.</para>
/// </summary>
public sealed class SystemRecycleBin : IRecycleBin
{
    /// <summary>One instance is enough - it holds nothing.</summary>
    public static readonly SystemRecycleBin Instance = new();

    /// <summary>Windows and macOS both have one. Everywhere else this is false, and a delete is
    /// planned - and previewed - as a permanent one (see the class remarks).</summary>
    public bool IsAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>False for a file on a network share (Windows and macOS) or a removable drive
    /// (Windows) - see <see cref="RecycleBinReach"/>. Never throws.</summary>
    public bool CanRecycle(string path) => IsAvailable && Reach(path) == NoBinReason.None;

    /// <summary>The words this platform uses. The confirmation says them out loud, so nobody has to
    /// wonder whether the track is gone or filed.</summary>
    public string Name =>
        OperatingSystem.IsMacOS() ? "Trash"
        : OperatingSystem.IsWindows() ? "Recycle Bin"
        : "recycle bin";

    public void Send(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("No path given.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("The file is not there any more.", path);

        // The backstop behind the preview: whatever the plan said, a file with no bin to go to is
        // never handed to a shell that would delete it for good instead.
        var reach = Reach(path);
        if (reach != NoBinReason.None)
            throw new IOException(RecycleBinReach.Describe(reach, Name));

        if (OperatingSystem.IsWindows()) { SendWindows(path); return; }
        if (OperatingSystem.IsMacOS()) { SendMac(path); return; }

        // Nothing calls this here - a plan built against IsAvailable == false is a permanent delete
        // and never comes through the bin. It throws rather than pretending, so a future caller that
        // skips the check gets a failure and not a vanished track.
        throw new PlatformNotSupportedException("There is no recycle bin on this system.");
    }

    /// <summary>
    /// Why this file has no bin to go to, if anything known says so. Never throws: a path that cannot
    /// be read is answered <see cref="NoBinReason.None"/>, and the shell - nuke warning on - has the
    /// last word.
    /// </summary>
    private static NoBinReason Reach(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return NoBinReason.None;

        try
        {
            var full = Path.GetFullPath(path);

            if (OperatingSystem.IsWindows())
                return RecycleBinReach.OnWindows(full, WindowsDriveType);

            // macOS: DriveInfo asks statfs about the volume holding ANY path - it does not have to
            // be the mount point - and maps the file system name to a type (smbfs, afpfs, nfs and
            // webdav read as Network). A USB stick does have a Trash on the Mac, so only the share
            // is refused here.
            if (OperatingSystem.IsMacOS())
                return new DriveInfo(full).DriveType == DriveType.Network
                    ? NoBinReason.NetworkShare
                    : NoBinReason.None;

            return NoBinReason.None;
        }
        catch (Exception)
        {
            return NoBinReason.None;
        }
    }

    /// <summary>GetDriveType through the BCL: DRIVE_REMOTE reads as Network, DRIVE_REMOVABLE as
    /// Removable. A drive that cannot be asked reads as Unknown.</summary>
    private static DriveType WindowsDriveType(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType;
        }
        catch (Exception)
        {
            return DriveType.Unknown;
        }
    }

    // -- Windows -----------------------------------------------------------------------------
    //
    // SHFileOperationW with FOF_ALLOWUNDO is the shell's own recycle - the same code path Explorer
    // takes, so the file lands in the bin with its original location recorded and "Restore" works.
    // Chosen over IFileOperation (COM, needs apartment plumbing) and over
    // Microsoft.VisualBasic.FileIO (an extra framework assembly, and it is a UI-aware wrapper around
    // exactly this call). No package is added for it.
    //
    // (!!) FOF_ALLOWUNDO is a request, not a promise. Per the SHFILEOPSTRUCT documentation, a file
    // the shell cannot recycle - on a network share, on a removable drive, larger than the bin will
    // hold, on a drive whose bin is switched off - is deleted PERMANENTLY instead, and with
    // FOF_NOCONFIRMATION that happens without a word. It does NOT come back as an error. So:
    //   - a network share or a removable drive is refused before the shell is asked (Send, through
    //     RecycleBinReach) - a per-file failure, and the file stays where it is;
    //   - FOF_WANTNUKEWARNING covers what no path can predict (too large, bin switched off): it is
    //     the one confirmation FOF_NOCONFIRMATION does not silence, so the shell asks before it
    //     destroys anything, and "No" leaves the file alone.

    private const uint FoDelete = 0x0003;

    private const ushort FofSilent = 0x0004;            // no progress dialog
    private const ushort FofNoConfirmation = 0x0010;    // our own confirmation already ran
    private const ushort FofAllowUndo = 0x0040;         // (!!) THE flag - without it this is permanent
    private const ushort FofNoErrorUi = 0x0400;         // report failures to us, not to a dialog
    private const ushort FofNoConfirmMkDir = 0x0200;
    private const ushort FofWantNukeWarning = 0x4000;   // (!!) ask before anything is destroyed, not recycled

    /// <summary>
    /// SHFILEOPSTRUCTW.
    ///
    /// <para>(!!) NO explicit <c>Pack</c>. shellapi.h wraps this struct in <c>pshpack1.h</c>, which
    /// makes a <c>Pack = 1</c> transliteration look obviously right - and it produces an access
    /// violation on x64, because the pointer fields then sit at unaligned offsets that the shell
    /// reads as garbage (verified: 0xC0000005 inside SHFileOperationW). Natural alignment is what
    /// the 64-bit shell actually expects. Do not "fix" this back.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref ShFileOpStruct op);

    private static void SendWindows(string path)
    {
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            // The shell wants a DOUBLE-null-terminated list. One trailing NUL ends the entry, the
            // second ends the list; the marshaller supplies the last one.
            pFrom = path + "\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofNoConfirmMkDir
                     | FofWantNukeWarning,
        };

        var result = SHFileOperationW(ref op);

        // "No" to the shell's permanent-delete question lands here, as an aborted operation.
        if (op.fAnyOperationsAborted)
            throw new IOException(File.Exists(path)
                ? "Not deleted - the delete was stopped, and this file is left where it is."
                : "The delete was stopped before it finished.");

        if (result != 0)
            throw new IOException(
                $"Windows would not put this in the Recycle Bin (shell error 0x{result:X}).");

        // The shell reports success for a few cases where nothing happened. The file being gone is
        // the only proof that counts.
        if (File.Exists(path))
            throw new IOException("The file is still there after the Recycle Bin reported success.");
    }

    // -- macOS -------------------------------------------------------------------------------
    //
    // The Finder's own "move to trash", asked for through osascript. It is the one route that
    // produces a real Trash entry with a "Put Back" target; NSFileManager.trashItemAtURL would need
    // an Objective-C bridge for a single call, and the raw ~/.Trash rename is not the same thing.

    private static void SendMac(string path)
    {
        // The path goes in as a POSIX file inside an AppleScript string literal, which has exactly
        // two characters that can break it: the backslash and the double quote. Both are legal in a
        // macOS filename, and the backslash HAS to be escaped FIRST - doing the quote first would
        // then escape the backslashes it just introduced and turn every \" back into a literal
        // backslash followed by a string-ending quote.
        var quoted = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script = $"tell application \"Finder\" to delete POSIX file \"{quoted}\"";

        var start = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);

        using var process = Process.Start(start)
            ?? throw new IOException("Could not ask the Finder to move this to the Trash.");

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new IOException(
                $"The Finder would not move this to the Trash: {error.Trim()}");

        if (File.Exists(path))
            throw new IOException("The file is still there after the Trash reported success.");
    }
}
