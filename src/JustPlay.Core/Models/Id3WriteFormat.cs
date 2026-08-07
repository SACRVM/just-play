namespace JustPlay.Core.Models;

/// <summary>
/// How JUST TAG writes ID3v2 tags to MP3 files - mp3tag's three write modes, plus the one mp3tag
/// doesn't have: leave the file's own version alone.
/// <para>
/// The default is <see cref="KeepFileVersion"/>. The three explicit modes CONVERT - that is their
/// point, and it is a deliberate act, not a side effect of pressing Save (Chloe 2026-08-04: "wir
/// wollten die tags nur sanft anpacken - ausser in just tag selbst, dann mit konvertierung"). A
/// conversion re-serialises the whole tag and re-encodes the GEOB <i>descriptors</i> Serato and
/// Mixed In Key look their cue points up by (measured 2026-07-31, 128 writes / 787 vendor frames).
/// </para>
/// <para>
/// Of the converting modes, <see cref="Id3v23Utf16"/> is mp3tag's default and the only combination
/// Windows Explorer / WMP / older car + DJ hardware read reliably; UTF-8 / v2.4 is what silently
/// breaks on that gear. (Research: <c>.claude/research/just-tag-tag-standards-and-mp3tag.md</c>.)
/// </para>
/// Applied process-globally via TagLib# static config in the metadata adapter - see
/// <see cref="JustPlay.Core.Abstractions.IMetadataWriter.ConfigureId3WriteFormat"/>. Non-MP3
/// containers (FLAC / MP4 / Ogg) ignore it.
/// </summary>
public enum Id3WriteFormat
{
    /// <summary>
    /// Convert nothing - a file keeps the ID3v2 version and text encodings it already has, and only
    /// the fields being written are touched. The default, and what JUST PLAY / the Pre-Cue Finder /
    /// the CLI do (they never call ConfigureId3WriteFormat, so TagLib#'s own
    /// <c>ForceDefault*</c> = false applies). A file with no ID3v2 tag at all gets a fresh v2.3/UTF-16 one.
    /// </summary>
    KeepFileVersion,

    /// <summary>Convert to ID3v2.3 + UTF-16 - mp3tag's default; maximum compatibility.</summary>
    Id3v23Utf16,

    /// <summary>Convert to ID3v2.3 + ISO-8859-1 (Latin-1) - for the oldest gear that chokes on UTF-16 BOMs (ASCII-safe text only).</summary>
    Id3v23Latin1,

    /// <summary>Convert to ID3v2.4 + UTF-8 - modern / compact; only for software known to read v2.4.</summary>
    Id3v24Utf8,
}
