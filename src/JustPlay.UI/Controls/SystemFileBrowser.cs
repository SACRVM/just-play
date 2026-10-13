using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace JustPlay.UI.Controls;

/// <summary>
/// Hand a path to the OS file manager - Explorer on Windows, Finder on macOS, whatever is registered
/// on Linux.
///
/// <para>Shared because three apps want it and two of them had already written it out inline (JUST
/// STREAM's recordings folder, twice). It is small enough to copy, which is exactly why it kept
/// getting copied.</para>
///
/// <para><b>Nothing here throws.</b> A pulled stick, a share that is no longer mounted, a machine
/// with no file manager registered - all of that is an ordinary state for a music library on a NAS,
/// not a reason to take a window down (the suite's never-crash rule). A failed reveal simply does
/// nothing visible.</para>
/// </summary>
public static class SystemFileBrowser
{
    /// <summary>The name this action goes by on this platform. One label would be wrong on one of
    /// them, and it is the label people look for by muscle memory.</summary>
    public static string RevealVerb =>
        OperatingSystem.IsMacOS() ? "Reveal in Finder"
        : OperatingSystem.IsWindows() ? "Show in Explorer"
        : "Show in file manager";

    /// <summary>Open a folder in the file manager.</summary>
    public static void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            // UseShellExecute so Windows resolves the folder to an Explorer window rather than
            // trying to execute it.
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception)
        {
            // See the class remarks: an unreachable path is a normal thing to click on.
        }
    }

    /// <summary>
    /// Open the folder a file lives in AND select the file itself. Falls back to just opening the
    /// folder where the platform has no way to say "and highlight this one" - which is still the
    /// useful half of the answer.
    /// </summary>
    public static void Reveal(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The argument is deliberately built as ONE string: explorer.exe wants
                // /select,"path" as a single token, and passing it through ArgumentList quotes the
                // comma form into something it does not understand.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true,
                });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                var open = new ProcessStartInfo { FileName = "open", UseShellExecute = false };
                open.ArgumentList.Add("-R");
                open.ArgumentList.Add(filePath);
                Process.Start(open);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                _ = RevealLinux(filePath);
                return;
            }

            OpenFolder(Path.GetDirectoryName(filePath) ?? "");
        }
        catch (Exception)
        {
            // Same rule as above.
        }
    }

    /// <summary>
    /// org.freedesktop.FileManager1.ShowItems - the "open the folder and select this" call Dolphin,
    /// Nautilus, Nemo and Thunar all answer on the session bus. A desktop whose file manager does
    /// not (or no session bus at all) gets the folder opened through xdg-open instead.
    /// </summary>
    private static async Task RevealLinux(string filePath)
    {
        try
        {
            var bus = DBusConnection.Session;
            await bus.CallMethodAsync(ShowItems(bus, new Uri(Path.GetFullPath(filePath)).AbsoluteUri));
        }
        catch (Exception)
        {
            OpenFolder(Path.GetDirectoryName(filePath) ?? "");
        }
    }

    // The writer is a ref struct and cannot live across an await, so the message is built here.
    private static MessageBuffer ShowItems(DBusConnection bus, string uri)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader("org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                                "org.freedesktop.FileManager1", "ShowItems", "ass", MessageFlags.None);
        var uris = w.WriteArrayStart(DBusType.String);
        w.WriteString(uri);
        w.WriteArrayEnd(uris);
        w.WriteString("");   // startup id: none
        return w.CreateMessage();
    }
}
