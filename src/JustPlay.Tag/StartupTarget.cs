using System;
using System.IO;
using System.Linq;

namespace JustPlay.Tag;

/// <summary>
/// What JUST TAG was launched ON, taken off the command line.
///
/// <para><c>JustTag.exe "D:\music\new drops"</c> opens that folder; <c>JustTag.exe "...\track.flac"</c>
/// opens the folder the file is in and lands on the file. This is the entry point the planned Explorer
/// integration ("Open in JUST TAG" on a folder's right-click menu) will call - the shell only ever hands
/// a program a path, so the path handling is the whole mechanism and it is worth having before the
/// registry keys exist. It also means you can already do it from a terminal or a shortcut.</para>
///
/// <para>Anything that is not there any more is simply dropped: a stale shortcut must land on the normal
/// start folder, never on an error at launch.</para>
/// </summary>
public sealed record StartupTarget(string? Folder, string? SelectFile)
{
    public static readonly StartupTarget None = new(null, null);

    public static StartupTarget From(string[]? args)
    {
        // Options may join the arguments later; today the first path-shaped one wins, and a path that
        // does not resolve is ignored rather than reported.
        foreach (var raw in args ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('-')) continue;

            string full;
            try { full = Path.GetFullPath(raw); }
            catch (Exception) { continue; }

            if (Directory.Exists(full)) return new StartupTarget(full, null);
            if (File.Exists(full) && Path.GetDirectoryName(full) is { Length: > 0 } dir)
                return new StartupTarget(dir, full);
        }

        return None;
    }
}
