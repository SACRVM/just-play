using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// A TXXX field is the same field in any casing. foobar2000 writes <c>replaygain_track_gain</c>, we
/// write <c>REPLAYGAIN_TRACK_GAIN</c>; a case-sensitive lookup missed theirs, so our write ADDED a
/// second gain frame next to the old one (two gains in one file, one of them stale) and the write
/// preview called an overwrite a fresh write.
/// </summary>
public sealed class CustomFieldCaseTests : IDisposable
{
    private readonly List<string> _temps = [];
    private readonly TagLibMetadataWriter _writer = new();

    public void Dispose()
    {
        foreach (var t in _temps)
            if (File.Exists(t)) File.Delete(t);
    }

    [Fact]
    public void Write_OverForeignLowercaseFrames_LeavesExactlyOneOfEach_InOurSpelling()
    {
        var path = Mp3With(("replaygain_track_gain", "-3.00 dB"), ("replaygain_track_peak", "0.900000"));

        _writer.Write(path, new TagWrite { ReplayGainDb = -6.35, Peak = 0.988553 });

        var gain = Assert.Single(Txxx(path, "REPLAYGAIN_TRACK_GAIN"));
        Assert.Equal("REPLAYGAIN_TRACK_GAIN", gain.Description);
        Assert.Equal("-6.35 dB", gain.Value);

        var peak = Assert.Single(Txxx(path, "REPLAYGAIN_TRACK_PEAK"));
        Assert.Equal("0.988553", peak.Value);
    }

    [Fact]
    public void Write_WithBothSpellingsPresent_CollapsesThemToOne()
    {
        var path = Mp3With(("replaygain_track_gain", "-3.00 dB"), ("REPLAYGAIN_TRACK_GAIN", "-4.00 dB"));

        _writer.Write(path, new TagWrite { ReplayGainDb = -6.35 });

        Assert.Equal("-6.35 dB", Assert.Single(Txxx(path, "REPLAYGAIN_TRACK_GAIN")).Value);
    }

    [Fact]
    public void Write_LeavesUnrelatedFramesAlone()
    {
        var path = Mp3With(("replaygain_track_gain", "-3.00 dB"), ("EnergyLevel", "6"));

        _writer.Write(path, new TagWrite { ReplayGainDb = -6.35 });

        Assert.Equal("6", Assert.Single(Txxx(path, "EnergyLevel")).Value);
    }

    [Fact]
    public void Preview_SeesAForeignLowercaseFrame_AsAnOverwrite()
    {
        var path = Mp3With(("replaygain_track_gain", "-3.00 dB"));

        var plan = new TagLibWritePreview().Preview(path, new TagWrite { ReplayGainDb = -6.35 },
                                                     TagWritePolicy.AllowAll);

        var field = Assert.Single(plan.Fields);
        Assert.Equal(TagWriteAction.Overwrite, field.Action);
        Assert.Equal("-3.00 dB", field.CurrentValue);
    }

    [Fact]
    public void Read_FindsAForeignSpelling()
    {
        var path = Mp3With(("energy", "7"));

        Assert.Equal(7, new TagLibMetadataReader().Read(path).TaggedEnergy);
    }

    [Fact]
    public void Restore_ToNoReplayGain_RemovesEverySpelling()
    {
        var path = Mp3With(("replaygain_track_gain", "-3.00 dB"), ("REPLAYGAIN_TRACK_GAIN", "-4.00 dB"),
                           ("Replaygain_Track_Peak", "0.900000"));

        _writer.Restore(path, new TagRestore());

        Assert.Empty(Txxx(path, "REPLAYGAIN_TRACK_GAIN"));
        Assert.Empty(Txxx(path, "REPLAYGAIN_TRACK_PEAK"));
    }

    // -- fixtures ---------------------------------------------------------------------------------

    /// <summary>A minimal ID3v2.3 MP3 (the TagContractComplianceTests shape) carrying the given TXXX
    /// frames verbatim - added as frames, so two spellings of one key really are two frames.</summary>
    private string Mp3With(params (string Description, string Value)[] frames)
    {
        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = 3;
        bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
        var path = Path.Combine(Path.GetTempPath(), $"justplay_txxxcase_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, bare);
        _temps.Add(path);

        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)!;
        foreach (var (description, value) in frames)
            id3.AddFrame(new TagLib.Id3v2.UserTextInformationFrame(description) { Text = [value] });
        file.Save();

        return path;
    }

    /// <summary>Every TXXX frame whose description matches <paramref name="key"/> in any casing.</summary>
    private static List<(string Description, string? Value)> Txxx(string path, string key)
    {
        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, false)!;
        return id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>("TXXX")
                  .Where(f => string.Equals(f.Description, key, StringComparison.OrdinalIgnoreCase))
                  .Select(f => (f.Description, f.Text.FirstOrDefault()))
                  .ToList();
    }
}
