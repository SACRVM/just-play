using JustPlay.Core.Tagging;
using JustPlay.Metadata;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// "Only the fields you edited are written" - through the three routes an editorial write takes in
/// the suite (the single-file save, the multi-file save, TRANSFORM), on real files, measured on the
/// bytes.
///
/// <para>What was wrong: every save wrote every editorial field back from a read that kept only the
/// first value. In ID3v2.3, TagLib# splits TPE1/TCON on '/', so fixing a title truncated "AC/DC" to
/// "AC" and "Techno (Peak Time / Driving)" to "Techno (Peak Time"; the year turned a TDRC date into
/// a bare year; the comment helper dropped iTunes' COMM frames and the other-language ones.</para>
/// </summary>
public sealed class EditorWritesOnlyEditedFieldsTests : IDisposable
{
    private readonly TagFileFixture _files = new();
    private readonly TagLibMetadataReader _reader = new();
    private readonly TagLibMetadataWriter _writer = new();

    public void Dispose() => _files.Dispose();

    private TagEditorViewModel Editor() => new(_reader, _writer);

    // -- One file ---------------------------------------------------------------------------------

    [Fact]
    public void TheEditor_ShowsTheWholeValue_NotTheFirstPart()
    {
        var vm = Editor();
        vm.Load(_files.Mp3(3));

        Assert.Equal("AC/DC", vm.Artist);
        Assert.Equal("Various / Others", vm.AlbumArtist);
        Assert.Equal("Techno (Peak Time / Driving)", vm.Genre);
        Assert.Equal("2019", vm.Year);
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void EditingOnlyTheTitle_LeavesEveryOtherFrameByteIdentical(byte major)
    {
        var path = _files.Mp3(major);
        var before = TagFileFixture.Id3Frames(path);

        var vm = Editor();
        vm.Load(path);
        vm.Title = "New Title";
        Assert.True(vm.Save());

        Assert.Equal(TagFileFixture.Except(before, "TIT2"),
                     TagFileFixture.Except(TagFileFixture.Id3Frames(path), "TIT2"));
        Assert.Equal("New Title", _reader.ReadEditable(path).Title);
    }

    [Fact]
    public void EditingOnlyTheTitle_OnFlac_KeepsBothArtists()
    {
        var path = _files.Flac();
        var before = TagFileFixture.XiphFields(path);

        var vm = Editor();
        vm.Load(path);
        Assert.Equal("Artist One; Artist Two", vm.Artist);

        vm.Title = "New Title";
        Assert.True(vm.Save());

        var after = TagFileFixture.XiphFields(path);
        Assert.Equal(["Artist One", "Artist Two"], after["ARTIST"]);
        before.Remove("TITLE");
        after.Remove("TITLE");
        Assert.Equal(before, after);
    }

    /// <summary>The other half: a field that IS edited goes in, as one value.</summary>
    [Fact]
    public void AnEditedArtist_IsWrittenAsOneValue()
    {
        var path = _files.Mp3(3);

        var vm = Editor();
        vm.Load(path);
        vm.Artist = "Ansome";
        Assert.True(vm.Save());

        var back = _reader.ReadEditable(path);
        Assert.Equal("Ansome", back.Artist);
        Assert.Equal("Techno (Peak Time / Driving)", back.Genre);
    }

    // -- A selection ------------------------------------------------------------------------------

    /// <summary>
    /// The rows come from the INDEX, which reads first values - so the selection "agrees" on ARTIST
    /// "AC" and GENRE "Techno (Peak Time", ticked by default. Left as they were, they are not written:
    /// they are not edits, and writing them back would truncate every file.
    /// </summary>
    [Fact]
    public async Task EditingOnlyTheTitle_AcrossASelection_LeavesEveryOtherFrameByteIdentical()
    {
        var a = _files.Mp3(3, title: "One");
        var b = _files.Mp3(3, title: "Two");
        var before = new[] { TagFileFixture.Id3Frames(a), TagFileFixture.Id3Frames(b) };

        var vm = Editor();
        vm.LoadMany([new TagTarget(a, _reader.Read(a)), new TagTarget(b, _reader.Read(b))]);
        Assert.True(vm.WriteArtist);                    // agreed on, so ticked - and untouched

        vm.WriteTitle = true;
        vm.Title = "Same Title";
        var report = await vm.SaveManyAsync();

        Assert.Equal(2, report.Written);
        Assert.Equal(0, report.Failed);
        foreach (var (path, i) in new[] { (a, 0), (b, 1) })
        {
            Assert.Equal(TagFileFixture.Except(before[i], "TIT2"),
                         TagFileFixture.Except(TagFileFixture.Id3Frames(path), "TIT2"));
            Assert.Equal("Same Title", _reader.ReadEditable(path).Title);
        }
        Assert.False(vm.IsDirty);
    }

    // -- TRANSFORM --------------------------------------------------------------------------------

    /// <summary>A transform works on the WHOLE value - the file's, read at write time - and writes
    /// nothing but the field it changed.</summary>
    [Fact]
    public async Task ATransform_ChangesTheWholeValue_AndNothingElse()
    {
        var path = _files.Mp3(3);
        var before = TagFileFixture.Id3Frames(path);

        var vm = new TagTransformViewModel(_reader, _writer, TagEditorViewModel.WriteDirect,
                                           [new TagTarget(path, _reader.Read(path))]);
        vm.DoArtist = false;
        vm.DoTitle = false;
        vm.DoGenre = true;
        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.Uppercase);

        var report = await vm.ApplyAsync();

        Assert.Equal(1, report.Written);
        Assert.Equal("TECHNO (PEAK TIME / DRIVING)", _reader.ReadEditable(path).Genre);
        Assert.Equal(TagFileFixture.Except(before, "TCON"),
                     TagFileFixture.Except(TagFileFixture.Id3Frames(path), "TCON"));
    }
}
