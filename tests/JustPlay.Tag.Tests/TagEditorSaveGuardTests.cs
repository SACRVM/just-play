using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// The editor's save, around the write itself: what it names, and what it reads first.
///
/// <para>What was wrong:</para>
/// <list type="bullet">
///   <item>A save wrote fields nobody edited - across a selection, the measurements it agreed on
///     too - and a file whose read failed was written anyway, from a blank snapshot.</item>
/// </list>
/// </summary>
public sealed class TagEditorSaveGuardTests
{
    /// <summary>Tags in memory, recording every write and the fields it named.</summary>
    private sealed class Store : IMetadataReader, IMetadataWriter
    {
        public readonly Dictionary<string, TrackMetadata> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Path, EditableField Fields)> Editorial = [];
        public readonly List<(string Path, TagWrite Write)> Analysis = [];

        public TrackMetadata Read(string filePath) =>
            Files.TryGetValue(filePath, out var m) ? m : new TrackMetadata { FallbackName = "x" };

        public EditableTags ReadEditable(string filePath) => EditorialWrite.From(Read(filePath));

        public void WriteEditable(string filePath, EditableTags tags, EditableField fields,
                                  CoverAction coverAction, byte[]? newCover, string? coverMimeType)
        {
            lock (Editorial) Editorial.Add((filePath, fields));
        }

        public void Write(string filePath, TagWrite write, TagWritePolicy? policy = null)
        {
            lock (Analysis) Analysis.Add((filePath, write));
        }

        public void Restore(string filePath, TagRestore restore) { }

        public void ConfigureId3WriteFormat(Id3WriteFormat format) { }
    }

    private const string A = "C:/music/a.mp3";
    private const string B = "C:/music/b.mp3";

    private static TrackMetadata Track(string title = "Old", double? bpm = 128) => new()
    {
        FallbackName = "track",
        Title = title,
        Artist = "Perc",
        Album = "Bulk",
        TaggedBpm = bpm,
    };

    private static Store Seed(params string[] paths)
    {
        var store = new Store();
        foreach (var p in paths) store.Files[p] = Track();
        return store;
    }

    private static List<TagTarget> Rows(Store store, params string[] paths) =>
        paths.Select(p => new TagTarget(p, store.Read(p))).ToList();

    // -- A failed read writes nothing ------------------------------------------------------------

    [Fact]
    public async Task A_file_that_cannot_be_read_is_failed_and_not_written()
    {
        var store = Seed(A, B);
        var vm = new TagEditorViewModel(store, store);
        vm.LoadMany(Rows(store, A, B));                  // the rows came off the index, intact
        store.Files[B] = store.Files[B] with { ReadFailed = true };   // ... and the share dropped

        vm.Title = "New";
        var report = await vm.SaveManyAsync();

        Assert.Equal(1, report.Written);
        Assert.Equal(1, report.Failed);
        Assert.Contains("b.mp3", report.FirstError);
        Assert.DoesNotContain(store.Editorial, w => w.Path == B);
    }

    // -- What a save names ------------------------------------------------------------------------

    [Fact]
    public void A_single_file_save_names_only_the_edited_fields()
    {
        var store = Seed(A);
        var vm = new TagEditorViewModel(store, store);
        vm.Load(A);

        vm.Title = "New";
        vm.Comment = "a note";
        Assert.True(vm.Save());

        Assert.Equal(EditableField.Title | EditableField.Comment, Assert.Single(store.Editorial).Fields);
        Assert.Empty(store.Analysis);
    }

    [Fact]
    public async Task A_selection_save_names_only_the_edited_fields_and_rewrites_no_measurement()
    {
        var store = Seed(A, B);
        var vm = new TagEditorViewModel(store, store);
        vm.LoadMany(Rows(store, A, B));
        Assert.True(vm.WriteBpm);                       // agreed on, so ticked - and untouched

        vm.Title = "New";
        await vm.SaveManyAsync();

        Assert.All(store.Editorial, w => Assert.Equal(EditableField.Title, w.Fields));
        Assert.Empty(store.Analysis);
    }

    [Fact]
    public async Task A_changed_measurement_across_a_selection_is_still_written()
    {
        var store = Seed(A, B);
        var vm = new TagEditorViewModel(store, store);
        vm.LoadMany(Rows(store, A, B));

        vm.BpmText = "130";
        await vm.SaveManyAsync();

        Assert.Equal(2, store.Analysis.Count);
        Assert.All(store.Analysis, w => Assert.Equal(130, w.Write.Bpm));
        Assert.Empty(store.Editorial);
    }
}
