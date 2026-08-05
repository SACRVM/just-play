using System;
using System.IO;
using JustPlay.Core.Models;

namespace JustPlay.Tag.Settings;

/// <summary>
/// Answers one question for the editor: <b>would saving this file change its ID3v2 version?</b>
/// <para>
/// It matters whenever the user has picked one of the three CONVERTING write formats, because those
/// set <c>TagLib.Id3v2.Tag.ForceDefaultVersion = true</c> and then every save normalises the file to
/// the configured version — in both directions. (In the default
/// <see cref="Id3WriteFormat.KeepFileVersion"/> mode nothing converts and there is nothing to warn
/// about; <see cref="MajorFor"/> returns null and the notice stays away.) Measured
/// 2026-07-31 (128 writes / 787 vendor frames, dossier
/// <c>~/.claude/skills/dj-metadata-interop/references/taglib-byte-preservation.md</c> §6): a version
/// change re-serialises the whole tag and re-encodes GEOB <i>descriptors</i>. No vendor byte is lost,
/// but Serato and Mixed In Key look their frames up BY that description string, so their cue points /
/// key / energy can become unfindable while nothing looks broken. Re-verified against the shipped
/// writer for this change: <c>F_mik_armstrong.mp3</c> (ID3v2.4) saved in the DEFAULT mode came back
/// ID3v2.3 with its <c>GEOB:CuePoints</c> descriptor rewritten Latin-1 → UTF-16+BOM.
/// </para>
/// <para>
/// Lives in the app, not in JustPlay.Metadata, because it is not tag logic — it reads the four
/// magic bytes at the head of the file and compares an enum. Nothing is parsed, TagLib# is not
/// involved, and the file is opened read-only and shared.
/// </para>
/// </summary>
internal static class Id3VersionProbe
{
    /// <summary>
    /// The ID3v2 major version at the head of <paramref name="path"/> (2, 3 or 4), or null when the
    /// file carries no leading ID3v2 tag — a fresh MP3, or a FLAC / MP4 / Ogg, none of which the
    /// write-format setting touches. Tags that are NOT at the head (the AIFF / WAV <c>ID3 </c> chunk)
    /// are deliberately out of scope: best-effort notice, never a false alarm.
    /// </summary>
    public static int? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 4) return null;
            Span<byte> head = stackalloc byte[4];
            fs.ReadExactly(head);
            if (head[0] != (byte)'I' || head[1] != (byte)'D' || head[2] != (byte)'3') return null;
            return head[3];
        }
        catch
        {
            return null; // unreadable / locked — say nothing rather than guess
        }
    }

    /// <summary>
    /// The ID3v2 major version a given write format converts a file TO on save, or null for
    /// <see cref="Id3WriteFormat.KeepFileVersion"/> — which converts nothing, so there is no target.
    /// </summary>
    public static int? MajorFor(Id3WriteFormat format) => format switch
    {
        Id3WriteFormat.KeepFileVersion => null,
        Id3WriteFormat.Id3v24Utf8 => 4,
        _ => 3,
    };

    /// <summary>Display name for a major version, e.g. <c>"ID3v2.3"</c>.</summary>
    public static string Name(int major) => $"ID3v2.{major}";
}
