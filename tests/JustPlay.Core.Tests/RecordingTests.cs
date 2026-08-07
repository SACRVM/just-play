using System;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Tests for the pure policy helpers in <see cref="Recording"/> - format resolution, file
/// extensions, and file-name construction. No IO, no BASS; see BassRecordingService in
/// JustPlay.Audio.Bass for the encoder-facing implementation this feeds.
/// </summary>
public class RecordingTests
{
    // -- Resolve -----------------------------------------------------------------------

    [Fact]
    public void Resolve_SameAsStream_Mp3_MirrorsCodecAndBitrate()
    {
        var (codec, bitrate) = Recording.Resolve(RecordingFormat.SameAsStream, StreamFormat.Mp3, 128);

        Assert.Equal(RecordingCodec.Mp3, codec);
        Assert.Equal(128, bitrate);
    }

    [Fact]
    public void Resolve_SameAsStream_Opus_MirrorsCodecAndBitrate()
    {
        var (codec, bitrate) = Recording.Resolve(RecordingFormat.SameAsStream, StreamFormat.Opus, 192);

        Assert.Equal(RecordingCodec.Opus, codec);
        Assert.Equal(192, bitrate);
    }

    [Theory]
    [InlineData(StreamFormat.Mp3)]
    [InlineData(StreamFormat.Opus)]
    public void Resolve_Mp3_320_IsAlways320Mp3_RegardlessOfStream(StreamFormat streamFormat)
    {
        var (codec, bitrate) = Recording.Resolve(RecordingFormat.Mp3_320, streamFormat, 96);

        Assert.Equal(RecordingCodec.Mp3, codec);
        Assert.Equal(320, bitrate);
    }

    [Theory]
    [InlineData(RecordingFormat.Flac, RecordingCodec.Flac)]
    [InlineData(RecordingFormat.Aiff, RecordingCodec.Aiff)]
    [InlineData(RecordingFormat.Wav, RecordingCodec.Wav)]
    public void Resolve_LosslessFormats_BitrateIsZero(RecordingFormat format, RecordingCodec expectedCodec)
    {
        var (codec, bitrate) = Recording.Resolve(format, StreamFormat.Mp3, 320);

        Assert.Equal(expectedCodec, codec);
        Assert.Equal(0, bitrate);
    }

    // -- FileExtension -----------------------------------------------------------------

    [Theory]
    [InlineData(RecordingCodec.Mp3, ".mp3")]
    [InlineData(RecordingCodec.Opus, ".opus")]
    [InlineData(RecordingCodec.Flac, ".flac")]
    [InlineData(RecordingCodec.Aiff, ".aiff")]
    [InlineData(RecordingCodec.Wav, ".wav")]
    public void FileExtension_AllCodecs(RecordingCodec codec, string expected)
    {
        Assert.Equal(expected, Recording.FileExtension(codec));
    }

    // -- BuildFileName -----------------------------------------------------------------

    private static readonly DateTime FixedNow = new(2026, 7, 4, 21, 30, 5);

    [Fact]
    public void BuildFileName_ExactFormat()
    {
        var name = Recording.BuildFileName(FixedNow, "My Stream", RecordingCodec.Mp3);

        Assert.Equal("2026-07-04 21-30-05 - My Stream.mp3", name);
    }

    [Fact]
    public void BuildFileName_InvalidChars_BecomeUnderscore()
    {
        var name = Recording.BuildFileName(FixedNow, "My/Str:eam?", RecordingCodec.Mp3);

        Assert.Equal("2026-07-04 21-30-05 - My_Str_eam_.mp3", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFileName_NullOrEmptyOrWhitespaceProfile_FallsBackToRecording(string? profileName)
    {
        var name = Recording.BuildFileName(FixedNow, profileName, RecordingCodec.Mp3);

        Assert.Equal("2026-07-04 21-30-05 - Recording.mp3", name);
    }

    [Theory]
    [InlineData(RecordingCodec.Mp3, ".mp3")]
    [InlineData(RecordingCodec.Opus, ".opus")]
    [InlineData(RecordingCodec.Flac, ".flac")]
    [InlineData(RecordingCodec.Aiff, ".aiff")]
    [InlineData(RecordingCodec.Wav, ".wav")]
    public void BuildFileName_CodecDrivesExtension(RecordingCodec codec, string expectedExtension)
    {
        var name = Recording.BuildFileName(FixedNow, "My Stream", codec);

        Assert.Equal($"2026-07-04 21-30-05 - My Stream{expectedExtension}", name);
    }
}
