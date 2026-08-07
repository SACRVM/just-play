using JustPlay.Core.Models;
using JustPlay.Metadata;

namespace JustPlay.Metadata.Tests;

/// <summary>
/// The ID3 write-format contract: <see cref="Id3WriteFormat.KeepFileVersion"/> converts NOTHING,
/// the other three convert everything they touch, and switching back and forth actually switches
/// (the statics are process-global, so a mode that forgets to clear a flag keeps converting
/// invisibly - that is the regression this file exists for).
/// <para>
/// Why it matters: a version change re-serialises the whole tag and re-encodes GEOB
/// <i>descriptors</i>, and Serato / Mixed In Key look their cue points up BY that descriptor string
/// (measured 2026-07-31, 128 writes / 787 vendor frames). Only JUST TAG ever calls
/// ConfigureId3WriteFormat; JUST PLAY, the Pre-Cue Finder and the CLI must keep running in the
/// non-forcing shape by never calling it at all.
/// </para>
/// Tests here mutate TagLib#'s static config, which is why this assembly runs its collections
/// serially (<c>xunit.runner.json</c>) and why the constructor + <see cref="Dispose"/> both put the
/// non-converting mode back.
/// </summary>
public sealed class Id3WriteFormatTests : IDisposable
{
    private readonly TagLibMetadataWriter _writer = new();
    private readonly List<string> _temps = [];

    public Id3WriteFormatTests() => _writer.ConfigureId3WriteFormat(Id3WriteFormat.KeepFileVersion);

    public void Dispose()
    {
        _writer.ConfigureId3WriteFormat(Id3WriteFormat.KeepFileVersion);
        foreach (var t in _temps)
            if (File.Exists(t)) File.Delete(t);
    }

    [Fact]
    public void KeepFileVersion_LeavesAnId3v24FileOnV24()
    {
        var file = NewMp3(major: 4);

        _writer.ConfigureId3WriteFormat(Id3WriteFormat.KeepFileVersion);
        _writer.WriteEditable(file, new EditableTags { Title = "kept" }, CoverAction.Keep, null, null);

        Assert.Equal(4, MajorVersionOf(file));
    }

    [Fact]
    public void KeepFileVersion_LeavesAnId3v23FileOnV23()
    {
        var file = NewMp3(major: 3);

        _writer.ConfigureId3WriteFormat(Id3WriteFormat.KeepFileVersion);
        _writer.WriteEditable(file, new EditableTags { Title = "kept" }, CoverAction.Keep, null, null);

        Assert.Equal(3, MajorVersionOf(file));
    }

    [Fact]
    public void ConvertModes_DoConvert_InBothDirections()
    {
        var up = NewMp3(major: 3);
        _writer.ConfigureId3WriteFormat(Id3WriteFormat.Id3v24Utf8);
        _writer.WriteEditable(up, new EditableTags { Title = "up" }, CoverAction.Keep, null, null);
        Assert.Equal(4, MajorVersionOf(up));

        // The DEFAULT-looking, "safe" mode is also the one that pulls a v2.4 file DOWN - the
        // direction that surprised us, and the reason converting is opt-in.
        var down = NewMp3(major: 4);
        _writer.ConfigureId3WriteFormat(Id3WriteFormat.Id3v23Utf16);
        _writer.WriteEditable(down, new EditableTags { Title = "down" }, CoverAction.Keep, null, null);
        Assert.Equal(3, MajorVersionOf(down));
    }

    [Fact]
    public void SwitchingBackToKeep_StopsConverting()
    {
        // Leave the process in a converting mode first - this is what a user does when they try a
        // mode in Settings and change their mind.
        _writer.ConfigureId3WriteFormat(Id3WriteFormat.Id3v23Utf16);
        _writer.ConfigureId3WriteFormat(Id3WriteFormat.KeepFileVersion);

        var file = NewMp3(major: 4);
        _writer.WriteEditable(file, new EditableTags { Title = "kept" }, CoverAction.Keep, null, null);

        Assert.Equal(4, MajorVersionOf(file));
        Assert.False(TagLib.Id3v2.Tag.ForceDefaultVersion);
        Assert.False(TagLib.Id3v2.Tag.ForceDefaultEncoding);
    }

    [Fact]
    public void KeepFileVersion_GivesAFreshTagTheMostReadableVersion()
    {
        // No ID3v2 tag at all: nothing to preserve, so the safe v2.3 is what gets created.
        var file = NewMp3(major: null);

        _writer.ConfigureId3WriteFormat(Id3WriteFormat.KeepFileVersion);
        _writer.WriteEditable(file, new EditableTags { Title = "fresh" }, CoverAction.Keep, null, null);

        Assert.Equal(3, MajorVersionOf(file));
    }

    // -- helpers --------------------------------------------------------------

    /// <summary>The ID3v2 major version in the file header, or null when there is no leading tag.</summary>
    private static int? MajorVersionOf(string path)
    {
        var head = new byte[4];
        using var fs = File.OpenRead(path);
        if (fs.Read(head) < 4) return null;
        return head[0] == 'I' && head[1] == 'D' && head[2] == '3' ? head[3] : null;
    }

    /// <summary>
    /// A throwaway MP3 carrying an ID3v2 tag of the requested major version (null = no tag at all).
    /// Same synthesis technique as <see cref="EditableTagsRoundTripTests"/>: a bare header plus one
    /// MPEG frame, expanded by TagLib# - which preserves the header version because the seeding save
    /// runs in the non-forcing mode.
    /// </summary>
    private string NewMp3(int? major)
    {
        var path = Path.Combine(Path.GetTempPath(), $"justplay_id3fmt_{Guid.NewGuid():N}.mp3");
        _temps.Add(path);

        // MPEG1 Layer3 128kbps 44100Hz stereo sync header.
        byte[] frame = [0xFF, 0xFB, 0x90, 0x00];

        if (major is not { } v)
        {
            File.WriteAllBytes(path, frame);
            return path;
        }

        var bare = new byte[14];
        bare[0] = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
        bare[3] = (byte)v; bare[4] = 0; bare[5] = 0; // major, revision, flags
        // bytes 6..9 stay 0 - a 0-byte tag body
        frame.CopyTo(bare, 10);
        File.WriteAllBytes(path, bare);

        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = "seed";
            f.Save();
        }

        return path;
    }
}
