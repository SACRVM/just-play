using System.Security.Cryptography;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// Integration tests for <see cref="TagLibWritePreview"/> - the read-only dry-run companion to
/// <see cref="TagLibMetadataWriter"/>. Real (temp) files, same fixture technique as
/// <see cref="TagContractComplianceTests"/> / <see cref="DjCommentRoundTripTests"/>: TagLib#
/// synthesises a minimal valid file so no binary fixture lives in the repo.
///
/// <para>
/// Every test either (a) proves the plan matches what <see cref="TagLibMetadataWriter"/> would
/// actually do (fresh file -> Write, already-written-with-the-same-values -> Unchanged, a
/// different existing value -> Overwrite, policy off -> SkippedByPolicy), or (b) proves the one
/// hard requirement: Preview never touches the file on disk.
/// </para>
/// </summary>
public sealed class TagLibWritePreviewTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly TagLibMetadataWriter _writer = new();
    private readonly TagLibWritePreview _preview = new();

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

    // -- Write: everything is empty on a fresh file -----------------------------

    [Fact]
    public void Preview_FreshFile_EveryRequestedFieldIsWrite()
    {
        var path = CreateMp3();

        var plan = _preview.Preview(path, FullCandidate(), TagWritePolicy.AllowAll);

        Assert.Equal(path, plan.FilePath);
        Assert.Equal(9, plan.Fields.Count); // Bpm, Key, Energy, JustPlayBlob, Comment, Grouping, Rating, ReplayGain x2
        Assert.All(plan.Fields, f => Assert.Equal(TagWriteAction.Write, f.Action));
        Assert.All(plan.Fields, f => Assert.Null(f.CurrentValue));
        Assert.True(plan.HasChanges);
        Assert.False(plan.HasOverwrites);
    }

    [Fact]
    public void Preview_OnlyFieldsTheCandidateSpecifies_ProduceRows()
    {
        var path = CreateMp3();

        var plan = _preview.Preview(path, new TagWrite { Bpm = 128 }, TagWritePolicy.AllowAll);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagFrameFamily.Bpm, field.Family);
    }

    // -- Unchanged: preview after writing the SAME candidate --------------------

    [Fact]
    public void Preview_AfterRealWrite_SameCandidate_IsUnchangedForEveryField()
    {
        var path = CreateMp3();
        var candidate = FullCandidate();

        _writer.Write(path, candidate);
        var plan = _preview.Preview(path, candidate, TagWritePolicy.AllowAll);

        Assert.All(plan.Fields, f => Assert.Equal(TagWriteAction.Unchanged, f.Action));
        Assert.False(plan.HasChanges);
        Assert.False(plan.HasOverwrites);
    }

    // -- Overwrite: a different value already occupies the field ----------------

    [Fact]
    public void Preview_DifferentBpmAlreadyPresent_IsOverwrite()
    {
        var path = CreateMp3();
        _writer.Write(path, new TagWrite { Bpm = 120 });

        var plan = _preview.Preview(path, new TagWrite { Bpm = 128 }, TagWritePolicy.AllowAll);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagFrameFamily.Bpm, field.Family);
        Assert.Equal(TagWriteAction.Overwrite, field.Action);
        Assert.Equal("120", field.CurrentValue);
        Assert.Equal("128", field.ProposedValue);
        Assert.True(plan.HasOverwrites);
    }

    [Fact]
    public void Preview_KeyWrittenAsMusicalNotationByAnotherTool_SameKeyComparesUnchanged()
    {
        var path = CreateMp3();
        // Seed the raw tag directly, as an older/foreign tagger would: musical name, not Camelot.
        SeedRawTag(path, tag => tag.InitialKey = "Gm"); // G minor == Camelot "6A"

        var plan = _preview.Preview(path, new TagWrite { Key = GMinor }, TagWritePolicy.AllowAll);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagWriteAction.Unchanged, field.Action);
        Assert.Equal("Gm", field.CurrentValue);
        Assert.Equal("6A", field.ProposedValue);
    }

    [Fact]
    public void Preview_DifferentKeyAlreadyPresent_IsOverwrite()
    {
        var path = CreateMp3();
        SeedRawTag(path, tag => tag.InitialKey = "8A"); // A minor - a different key from GMinor.

        var plan = _preview.Preview(path, new TagWrite { Key = GMinor }, TagWritePolicy.AllowAll);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagWriteAction.Overwrite, field.Action);
        Assert.Equal("8A", field.CurrentValue);
        Assert.Equal("6A", field.ProposedValue);
    }

    [Fact]
    public void Preview_RemovingAnExistingLike_IsOverwrite()
    {
        var path = CreateMp3();
        _writer.Write(path, new TagWrite { Favorite = true });

        var plan = _preview.Preview(path, new TagWrite { Favorite = false }, TagWritePolicy.AllowAll);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagWriteAction.Overwrite, field.Action);
        Assert.Equal("Liked", field.CurrentValue);
        Assert.Equal("Not liked", field.ProposedValue);
    }

    [Fact]
    public void Preview_UnlikingAnAlreadyUnlikedFile_IsUnchanged()
    {
        var path = CreateMp3();

        var plan = _preview.Preview(path, new TagWrite { Favorite = false }, TagWritePolicy.AllowAll);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagWriteAction.Unchanged, field.Action);
    }

    // -- SkippedByPolicy: the policy wins even over an otherwise-empty field ----

    [Fact]
    public void Preview_PolicyDisallowsFamily_IsSkippedByPolicy_EvenOnAnEmptyField()
    {
        var path = CreateMp3();
        var policy = TagWritePolicy.AllowAll with { AllowBpm = false };

        var plan = _preview.Preview(path, new TagWrite { Bpm = 128 }, policy);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagWriteAction.SkippedByPolicy, field.Action);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void Preview_PolicyDisallowsFamily_IsSkippedByPolicy_EvenWhenItWouldHaveBeenOverwrite()
    {
        var path = CreateMp3();
        _writer.Write(path, new TagWrite { Bpm = 120 });
        var policy = TagWritePolicy.AllowAll with { AllowBpm = false };

        var plan = _preview.Preview(path, new TagWrite { Bpm = 128 }, policy);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagWriteAction.SkippedByPolicy, field.Action);
        Assert.False(plan.HasOverwrites); // the field is reported as skipped, not double-counted
    }

    // -- ReplayGain: one family, two rows ----------------------------------------

    [Fact]
    public void Preview_ReplayGain_ProducesTwoRowsUnderOneFamily()
    {
        var path = CreateMp3();

        var plan = _preview.Preview(path,
            new TagWrite { ReplayGainDb = -6.35, Peak = 0.988553 }, TagWritePolicy.AllowAll);

        Assert.Equal(2, plan.Fields.Count);
        Assert.All(plan.Fields, f => Assert.Equal(TagFrameFamily.ReplayGain, f.Family));
        Assert.Contains(plan.Fields, f => f.FieldName == "ReplayGain (track gain)");
        Assert.Contains(plan.Fields, f => f.FieldName == "ReplayGain (peak)");
    }

    // -- The read-only guarantee ----------------------------------------------

    [Fact]
    public void Preview_NeverModifiesFileOnDisk()
    {
        var path = CreateMp3();
        var before = SHA256.HashData(File.ReadAllBytes(path));

        _preview.Preview(path, FullCandidate(), TagWritePolicy.AllowAll);
        _preview.Preview(path, FullCandidate(), TagWritePolicy.AllowAll with { AllowBpm = false, AllowComment = false });
        _preview.Preview(path, new TagWrite { Favorite = false }, TagWritePolicy.AllowAll);

        var after = SHA256.HashData(File.ReadAllBytes(path));
        Assert.Equal(before, after);
    }

    // -- FLAC / Xiph path -----------------------------------------------------

    [Fact]
    public void Preview_Flac_AfterRealWrite_SameCandidate_IsUnchanged()
    {
        var path = CreateFlac();
        var candidate = new TagWrite { Bpm = 128, Key = GMinor, Energy = 8 };

        _writer.Write(path, candidate);
        var plan = _preview.Preview(path, candidate, TagWritePolicy.AllowAll);

        Assert.Equal(3, plan.Fields.Count);
        Assert.All(plan.Fields, f => Assert.Equal(TagWriteAction.Unchanged, f.Action));
    }

    [Fact]
    public void Preview_Flac_FreshFile_EveryRequestedFieldIsWrite()
    {
        var path = CreateFlac();

        var plan = _preview.Preview(path,
            new TagWrite { Bpm = 128, Key = GMinor, Energy = 8 }, TagWritePolicy.AllowAll);

        Assert.All(plan.Fields, f => Assert.Equal(TagWriteAction.Write, f.Action));
    }

    // -- fixture builders (same technique as TagContractComplianceTests) --------

    private void SeedRawTag(string path, Action<TagLib.Tag> mutate)
    {
        using var file = TagLib.File.Create(path);
        mutate(file.Tag);
        file.Save();
    }

    private string CreateMp3()
    {
        // Bare ID3v2.3 header + one MPEG1 Layer3 sync header - TagLib parses it as a taggable MP3.
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = 3; // v2.3
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        return WriteTemp(".mp3", bare);
    }

    private string CreateFlac()
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
        return WriteTemp(".flac", ms.ToArray());
    }

    private string WriteTemp(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_preview_{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }
}
