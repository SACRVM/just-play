using System;
using System.IO;

namespace JustPlay.Core.Models;

/// <summary>
/// The user-facing recording format choice, persisted in settings. This is POLICY -
/// <see cref="Recording.Resolve"/> turns it into a concrete <see cref="RecordingJob"/>
/// at record-start time (that's when the active stream profile's codec/bitrate are known).
/// </summary>
public enum RecordingFormat
{
    /// <summary>
    /// Mirror the active stream profile's codec + bitrate. The self-check default: the file
    /// contains the same artifacts your listeners hear - encode at 128 kbps and you get the
    /// 128 kbps truth on disk (Chloe, 2026-07-04: "wie soll man sonst erfahren wie
    /// beschissen man live klingt?"). Note this mirrors CODEC quality, not connection
    /// quality - network dropouts the audience heard are not in the file.
    /// </summary>
    SameAsStream = 0,

    /// <summary>MP3 CBR 320 kbps - the compressed archive choice, plays everywhere.</summary>
    Mp3_320 = 1,

    /// <summary>FLAC lossless - the archive format: ~half of WAV, tags, bit-exact.</summary>
    Flac = 2,

    /// <summary>AIFF 16-bit lossless - the native lossless format of the Mac DJ world.</summary>
    Aiff = 3,

    /// <summary>WAV 16-bit lossless - maximum compatibility, biggest files.</summary>
    Wav = 4,
}

/// <summary>Concrete codec a recording is encoded with (resolved, no policy left).</summary>
public enum RecordingCodec
{
    Mp3,
    Opus,
    Flac,
    Aiff,
    Wav,
}

/// <summary>
/// A fully resolved recording spec, ready for <see cref="Abstractions.IRecordingService.StartAsync"/>.
/// </summary>
/// <param name="Codec">Concrete output codec.</param>
/// <param name="BitrateKbps">Encoder bitrate for <see cref="RecordingCodec.Mp3"/> /
/// <see cref="RecordingCodec.Opus"/>; ignored (0) for the lossless codecs.</param>
/// <param name="FilePath">Full target path including the file name and extension.</param>
/// <param name="TrimSilence">Auto-trim (default ON): don't WRITE silence instead of cutting it
/// afterwards. The typical gig order is "go on air first, start the music later" (Chloe,
/// 2026-07-04) - so REC only ARMS the recorder. A look-behind buffer feeds the encoder: the
/// file starts on the first beat and ends on the last with ZERO dead air at either end; short
/// breaks (<=30 s) are preserved 1:1, longer gaps are cut. No post-processing, identical for
/// every codec.</param>
public sealed record RecordingJob(RecordingCodec Codec, int BitrateKbps, string FilePath, bool TrimSilence = true);

/// <summary>
/// Pure policy helpers for recording - format resolution and file naming. No IO, no BASS,
/// fully unit-testable (see RecordingTests in JustPlay.Core.Tests).
/// </summary>
public static class Recording
{
    /// <summary>
    /// Resolve the persisted <see cref="RecordingFormat"/> into a concrete codec + bitrate.
    /// <see cref="RecordingFormat.SameAsStream"/> mirrors the active profile's codec and
    /// bitrate; everything else is fixed by the format itself.
    /// </summary>
    public static (RecordingCodec Codec, int BitrateKbps) Resolve(
        RecordingFormat format, StreamFormat streamFormat, int streamBitrateKbps) => format switch
    {
        RecordingFormat.SameAsStream => (
            streamFormat == StreamFormat.Opus ? RecordingCodec.Opus : RecordingCodec.Mp3,
            streamBitrateKbps),
        RecordingFormat.Mp3_320 => (RecordingCodec.Mp3, 320),
        RecordingFormat.Flac    => (RecordingCodec.Flac, 0),
        RecordingFormat.Aiff    => (RecordingCodec.Aiff, 0),
        RecordingFormat.Wav     => (RecordingCodec.Wav, 0),
        _ => (RecordingCodec.Mp3, 320),
    };

    /// <summary>File extension (with dot) for a codec. Opus files use the .opus Ogg convention.</summary>
    public static string FileExtension(RecordingCodec codec) => codec switch
    {
        RecordingCodec.Mp3  => ".mp3",
        RecordingCodec.Opus => ".opus",
        RecordingCodec.Flac => ".flac",
        RecordingCodec.Aiff => ".aiff",
        RecordingCodec.Wav  => ".wav",
        _ => ".mp3",
    };

    /// <summary>
    /// Build a recording file name: <c>"2026-07-04 21-30-05 - My Stream.mp3"</c>.
    /// Date-first so folders sort chronologically; seconds included so stop/start within the
    /// same minute never collides. The profile name is sanitized for the file system -
    /// invalid characters become '_', an empty/whitespace name falls back to "Recording".
    /// </summary>
    /// <param name="localNow">Local wall-clock time at record start.</param>
    /// <param name="profileName">Display name of the active server profile (or any label).</param>
    /// <param name="codec">Resolved codec - determines the extension.</param>
    public static string BuildFileName(DateTime localNow, string? profileName, RecordingCodec codec)
    {
        var name = (profileName ?? string.Empty).Trim();
        if (name.Length == 0)
            name = "Recording";

        name = FileNames.Sanitize(name);

        return $"{localNow:yyyy-MM-dd HH-mm-ss} - {name}{FileExtension(codec)}";
    }
}
