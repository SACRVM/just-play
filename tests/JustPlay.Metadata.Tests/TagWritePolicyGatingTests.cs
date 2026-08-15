using System.Security.Cryptography;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// 0.6 milestone P3 (JustPlay milestone doc, decision 3): <see cref="TagLibMetadataWriter.Write"/>
/// grew an optional <see cref="TagWritePolicy"/> parameter so the (future) UI half of P3 has a
/// real gate to switch. This file pins the two claims that made adding it safe:
///
/// <list type="number">
///   <item>
///   <b>The default is exactly today's ungated writer.</b> A caller that omits the argument
///   (every existing caller - <c>MainWindowViewModel</c>, the CLI, JUST TAG) must get
///   byte-identical output to a caller that passes <see cref="TagWritePolicy.AllowAll"/>
///   explicitly. The one-off proof against the real 8-file ID3 corpus (pre-change writer vs.
///   post-change writer, both hashed) lives in the L5 task report, not in the repo (the corpus is
///   gitignored, copyrighted, real music); these tests are the permanent, CI-repeatable version of
///   the same claim, on synthetic fixtures (same technique as <see cref="TagLibWritePreviewTests"/>).
///   </item>
///   <item>
///   <b>Each <see cref="TagFrameFamily"/> guard gates only its own field.</b> Denying one family
///   in <c>WriteCore</c> must leave that field exactly as it was, while every other field the
///   candidate <see cref="TagWrite"/> requested still gets written.
///   </item>
/// </list>
/// </summary>
public sealed class TagWritePolicyGatingTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibMetadataReader _reader = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles.Where(File.Exists)) File.Delete(f);
    }

    // G minor: pitch class 7, minor -> Camelot "6A".
    private static readonly MusicalKey GMinor = new(7, KeyMode.Minor);

    private static TrackAnalysisState SampleState() => new()
    {
        Version = TrackAnalysisState.CurrentVersion,
        Detected = new AnalysisResult { Bpm = 128, Key = GMinor, Energy = 8 },
    };

    // One value per family, all requested at once - the shape PromoteCommand/Persist build.
    private static TagWrite FullCandidate() => new()
    {
        Bpm = 128,
        Key = GMinor,
        Energy = 8,
        State = SampleState(),
        Comment = "6A - Energy 8",
        Grouping = "JP vibe",
        Favorite = true,
        ReplayGainDb = -6.35,
        Peak = 0.988553,
    };

    // -- 1. Omitting the policy argument is indistinguishable from AllowAll -----

    [Fact]
    public void Write_OmittedPolicyArgument_IsByteIdenticalTo_ExplicitAllowAll()
    {
        var seed = SeedBytes();
        var pathDefault = WriteTemp(".mp3", seed);
        var pathAllowAll = WriteTemp(".mp3", seed);

        _writer.Write(pathDefault, FullCandidate());                            // 2-arg - relies on the default
        _writer.Write(pathAllowAll, FullCandidate(), TagWritePolicy.AllowAll);  // 3-arg - explicit

        Assert.Equal(Hash(pathDefault), Hash(pathAllowAll));
    }

    [Fact]
    public void Write_OmittedPolicyArgument_IsByteIdenticalTo_ExplicitNull()
    {
        var seed = SeedBytes();
        var pathOmitted = WriteTemp(".mp3", seed);
        var pathNull = WriteTemp(".mp3", seed);

        _writer.Write(pathOmitted, FullCandidate());
        _writer.Write(pathNull, FullCandidate(), null);

        Assert.Equal(Hash(pathOmitted), Hash(pathNull));
    }

    [Fact]
    public void Write_OmittedPolicyArgument_OnFlac_IsByteIdenticalTo_ExplicitAllowAll()
    {
        var seed = SeedFlacBytes();
        var pathDefault = WriteTemp(".flac", seed);
        var pathAllowAll = WriteTemp(".flac", seed);
        var candidate = new TagWrite { Bpm = 128, Key = GMinor, Energy = 8 };

        _writer.Write(pathDefault, candidate);
        _writer.Write(pathAllowAll, candidate, TagWritePolicy.AllowAll);

        Assert.Equal(Hash(pathDefault), Hash(pathAllowAll));
    }

    // -- 2. Per-family gating - denying one family leaves only that field alone -

    [Fact]
    public void AllowBpm_False_LeavesBpmUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowBpm = false });

        var meta = _reader.Read(path);
        Assert.Null(meta.TaggedBpm);
        AssertEverythingElseWritten(path, meta, except: TagFrameFamily.Bpm);
    }

    [Fact]
    public void AllowKey_False_LeavesKeyUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowKey = false });

        var meta = _reader.Read(path);
        Assert.Null(meta.TaggedKey);
        AssertEverythingElseWritten(path, meta, except: TagFrameFamily.Key);
    }

    [Fact]
    public void AllowEnergy_False_LeavesEnergyUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowEnergy = false });

        var meta = _reader.Read(path);
        Assert.Null(meta.TaggedEnergy);
        AssertEverythingElseWritten(path, meta, except: TagFrameFamily.Energy);
    }

    [Fact]
    public void AllowJustPlayBlob_False_LeavesBlobUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowJustPlayBlob = false });

        var meta = _reader.Read(path);
        Assert.Null(meta.StoredAnalysis);
        AssertEverythingElseWritten(path, meta, except: TagFrameFamily.JustPlayBlob);
    }

    [Fact]
    public void AllowComment_False_LeavesCommentUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowComment = false });

        var meta = _reader.Read(path);
        Assert.Null(meta.Comment);
        AssertEverythingElseWritten(path, meta, except: TagFrameFamily.Comment);
    }

    [Fact]
    public void AllowGrouping_False_LeavesGroupingUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowGrouping = false });

        var meta = _reader.Read(path);
        Assert.Null(meta.Grouping);
        AssertEverythingElseWritten(path, meta, except: TagFrameFamily.Grouping);
    }

    [Fact]
    public void AllowRating_False_LeavesFavoriteUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowRating = false });

        var meta = _reader.Read(path);
        Assert.False(meta.IsFavorite);
        AssertEverythingElseWritten(path, meta, except: TagFrameFamily.Rating);
    }

    [Fact]
    public void AllowReplayGain_False_LeavesBothReplayGainFieldsUnset_WritesEverythingElse()
    {
        var path = CreateMp3();
        _writer.Write(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowReplayGain = false });

        Assert.Null(ReadTxxx(path, "REPLAYGAIN_TRACK_GAIN"));
        Assert.Null(ReadTxxx(path, "REPLAYGAIN_TRACK_PEAK"));
        AssertEverythingElseWritten(path, _reader.Read(path), except: TagFrameFamily.ReplayGain);
    }

    // -- shared assertion: every family OTHER than `except` must have applied ---

    private void AssertEverythingElseWritten(string path, TrackMetadata meta, TagFrameFamily except)
    {
        if (except != TagFrameFamily.Bpm) Assert.Equal(128.0, meta.TaggedBpm ?? 0.0);
        if (except != TagFrameFamily.Key) Assert.Equal("6A", meta.TaggedKey);
        if (except != TagFrameFamily.Energy) Assert.Equal(8, meta.TaggedEnergy ?? -1);
        if (except != TagFrameFamily.JustPlayBlob) Assert.NotNull(meta.StoredAnalysis);
        if (except != TagFrameFamily.Comment) Assert.Equal("6A - Energy 8", meta.Comment);
        if (except != TagFrameFamily.Grouping) Assert.Equal("JP vibe", meta.Grouping);
        if (except != TagFrameFamily.Rating) Assert.True(meta.IsFavorite);
        if (except != TagFrameFamily.ReplayGain)
        {
            Assert.Equal("-6.35 dB", ReadTxxx(path, "REPLAYGAIN_TRACK_GAIN"));
            Assert.Equal("0.988553", ReadTxxx(path, "REPLAYGAIN_TRACK_PEAK"));
        }
    }

    private static string? ReadTxxx(string path, string desc)
    {
        using var file = TagLib.File.Create(path);
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag id3) return null;
        return TagLib.Id3v2.UserTextInformationFrame.Get(id3, desc, false)?.Text is { Length: > 0 } t
            ? t[0] : null;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // -- fixture builders (same technique as TagLibWritePreviewTests) -----------

    private static byte[] SeedBytes()
    {
        // Bare ID3v2.3 header + one MPEG1 Layer3 sync header - TagLib parses it as a taggable MP3.
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = 3; // v2.3
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        return bare;
    }

    private static byte[] SeedFlacBytes()
    {
        // Minimal FLAC: "fLaC" + a last-block STREAMINFO (34 bytes) - enough for TagLib to open
        // the file and add a VORBIS_COMMENT block on save.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("fLaC"u8.ToArray());
        w.Write((byte)0x80);                       // last-block flag | type 0 (STREAMINFO)
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)34);
        var si = new byte[34];
        si[0] = 0x10; si[1] = 0x00;                 // min block size 4096
        si[2] = 0x10; si[3] = 0x00;                 // max block size 4096
        si[10] = 0x0A; si[11] = 0xC4; si[12] = 0x42; si[13] = 0xF0; // 44100 Hz, 2ch, 16bps
        w.Write(si);
        return ms.ToArray();
    }

    private string CreateMp3() => WriteTemp(".mp3", SeedBytes());

    private string WriteTemp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_policygate_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }
}
