namespace JustPlay.Core.Models;

/// <summary>
/// How JUST TAG writes ID3v2 tags to MP3 files — mirrors mp3tag's three-mode write setting.
/// <para>
/// The default (<see cref="Id3v23Utf16"/>) is mp3tag's default and the only combination Windows
/// Explorer / WMP / older car + DJ hardware read reliably; UTF-8 / v2.4 is what silently breaks on
/// that gear. (Research: <c>.claude/research/just-tag-tag-standards-and-mp3tag.md</c>.)
/// </para>
/// Applied process-globally via TagLib# static config in the metadata adapter — see
/// <see cref="JustPlay.Core.Abstractions.IMetadataWriter.ConfigureId3WriteFormat"/>. Non-MP3
/// containers (FLAC / MP4 / Ogg) ignore it.
/// </summary>
public enum Id3WriteFormat
{
    /// <summary>ID3v2.3 + UTF-16 — the safe default (mp3tag's default; maximum compatibility).</summary>
    Id3v23Utf16,

    /// <summary>ID3v2.3 + ISO-8859-1 (Latin-1) — for the oldest gear that chokes on UTF-16 BOMs (ASCII-safe text only).</summary>
    Id3v23Latin1,

    /// <summary>ID3v2.4 + UTF-8 — modern / compact; only for software known to read v2.4.</summary>
    Id3v24Utf8,
}
