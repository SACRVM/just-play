using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// The editor's save, around the write itself: what it names, what it reads first, and what it does
/// while it is running.
///
/// <para>What was wrong:</para>
/// <list type="bullet">
///   <item>A save wrote fields nobody edited - across a selection, the measurements it agreed on
///     too - and a file whose read failed was written anyway, from a blank snapshot.</item>
///   <item>A selection's save ran in the background with Save still enabled and the editor still
///     movable. A double click ran two loops over the same files; retargeting mid-save and choosing
///     Discard let the finishing save mark the NEW selection clean and drop its edits.</item>
///   <item>A selection's save with failures reported the editor clean, so the host moved on.</item>
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
    private const string C = "C:/music/c.mp3";
    private const string D = "C:/music/d.mp3";

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

    /// <summary>An executor that holds every write until the gate opens - a save "in flight".</summary>
    private static TagWriteExecutor Held(ManualResetEventSlim gate) => (path, write) =>
    {
        gate.Wait(TimeSpan.FromSeconds(10));
        write(path);
        return TagWriteOutcome.Written;
    };

    // -- B6: nothing moves while a selection is being saved ---------------------------------------

    [Fact]
    public async Task A_second_save_while_one_runs_does_nothing()
    {
        using var gate = new ManualResetEventSlim();
        var store = Seed(A, B);
        var vm = new TagEditorViewModel(store, store, Held(gate));
        vm.LoadMany(Rows(store, A, B));
        vm.Title = "New";
        Assert.True(vm.CanSave);

        var first = vm.SaveManyAsync();
        Assert.True(vm.IsSaving);
        Assert.False(vm.CanSave);                       // Save and Revert are off

        var second = await vm.SaveManyAsync();
        Assert.Equal(0, second.Written + second.Unchanged + second.Failed);

        gate.Set();
        var report = await first;

        Assert.Equal(2, report.Written);
        Assert.Equal(2, store.Editorial.Count);         // one loop over the files, not two
        Assert.False(vm.IsSaving);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task A_retarget_during_a_save_is_refused_and_the_saved_state_stays_with_its_files()
    {
        using var gate = new ManualResetEventSlim();
        var store = Seed(A, B, C, D);
        var vm = new TagEditorViewModel(store, store, Held(gate));
        vm.LoadMany(Rows(store, A, B));
        vm.Title = "New";

        var save = vm.SaveManyAsync();
        vm.LoadMany(Rows(store, C, D));
        vm.Load(C);
        vm.Clear();
        Assert.True(vm.SelectedPaths.SetEquals([A, B]));

        gate.Set();
        await save;

        Assert.True(vm.SelectedPaths.SetEquals([A, B]));
        Assert.Equal("New", vm.Title);
        Assert.False(vm.IsDirty);
    }

    /// <summary>The fields stay editable while the files are written, so anything typed meanwhile is
    /// NOT in them - it must stay unsaved rather than be absorbed into the "saved" state.</summary>
    [Fact]
    public async Task An_edit_typed_while_the_save_runs_stays_unsaved()
    {
        using var gate = new ManualResetEventSlim();
        var store = Seed(A, B);
        var vm = new TagEditorViewModel(store, store, Held(gate));
        vm.LoadMany(Rows(store, A, B));
        vm.Title = "New";

        var save = vm.SaveManyAsync();
        vm.Album = "Typed meanwhile";
        gate.Set();
        await save;

        Assert.True(vm.IsDirty);
        var sets = vm.BuildPlan().Sets;
        Assert.Contains(sets, s => s.Field == "ALBUM");
        Assert.DoesNotContain(sets, s => s.Field == "TITLE");
    }

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

    // -- A save with a failure is not a clean save ------------------------------------------------

    /// <summary>A file that failed does not hold the edits, so the edits are not saved: the editor
    /// stays dirty and the leave question answers "stay", instead of letting the host move on with
    /// the failure left in a log line.</summary>
    [Fact]
    public async Task A_save_with_a_failed_file_leaves_the_editor_dirty_until_a_retry_lands()
    {
        var store = Seed(A, B);
        var vm = new TagEditorViewModel(store, store);
        vm.LoadMany(Rows(store, A, B));
        store.Files[B] = store.Files[B] with { ReadFailed = true };

        vm.Title = "New";
        await vm.SaveManyAsync();
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSave);

        // The share is back: saving again finishes the job, and only then is the editor clean.
        store.Files[B] = store.Files[B] with { ReadFailed = false };
        var retry = await vm.SaveManyAsync();

        Assert.Equal(0, retry.Failed);
        Assert.False(vm.IsDirty);
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
