using System;
using System.Collections.Generic;
using System.IO;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// ANALYSIS and RAW are the two PER-FILE tabs, and they must appear and disappear together.
///
/// <para>They did not: ANALYSIS asked "not a multi-selection?", RAW asked "not a multi-selection AND
/// there is a file". The two agree everywhere except with NOTHING selected, where the strip offered
/// ANALYSIS - over an empty panel - and withheld RAW. Same question, two spellings, one state where
/// they disagreed.</para>
///
/// <para>Fixed by giving both the one <c>IsSingleFile</c> gate, and pinned here by asserting the two
/// flags are EQUAL in every state rather than asserting each one's value: it is their agreement that
/// is the rule, and a future third per-file tab is meant to join the same gate.</para>
/// </summary>
public class PerFileTabGateTests : IDisposable
{
    private sealed class FakeTags : IMetadataReader, IMetadataWriter
    {
        public TrackMetadata Read(string filePath) =>
            new() { FallbackName = Path.GetFileName(filePath) };

        public EditableTags ReadEditable(string filePath) => EditorialWrite.From(Read(filePath));

        public void WriteEditable(string filePath, EditableTags tags, CoverAction coverAction,
                                  byte[]? newCover, string? coverMimeType) { }

        public void Write(string filePath, TagWrite write, TagWritePolicy? policy = null) { }

        public void Restore(string filePath, TagRestore restore) { }

        public void ConfigureId3WriteFormat(Id3WriteFormat format) { }
    }

    private sealed class FakeRawReader : IRawTagReader
    {
        public RawTagReadResult Read(string filePath) =>
            new() { FilePath = filePath, Containers = [] };
    }

    private readonly List<string> _temp = [];

    /// <summary>Real (empty) files on disk: Load reads the file's size for its info line, and a test
    /// that pointed the editor at a path that does not exist would be exercising the failure path
    /// instead of the one being pinned.</summary>
    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"justtag_tabgate_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, []);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch (IOException) { }
        }
    }

    private static TagEditorViewModel Editor() =>
        new(new FakeTags(), new FakeTags(), rawReader: new FakeRawReader());

    [Fact]
    public void NothingSelected_HidesBothPerFileTabs()
    {
        var vm = Editor();

        Assert.False(vm.IsSingleFile);
        Assert.False(vm.CanShowAnalysis);
        Assert.False(vm.CanShowRaw);
    }

    [Fact]
    public void ClearingTheSelection_HidesBothPerFileTabs()
    {
        var vm = Editor();
        vm.Load(TempFile());
        Assert.True(vm.CanShowAnalysis);   // it was there a moment ago

        vm.Clear();

        Assert.False(vm.CanShowAnalysis);
        Assert.False(vm.CanShowRaw);
    }

    [Fact]
    public void OneFile_ShowsBothPerFileTabs()
    {
        var vm = Editor();
        vm.Load(TempFile());

        Assert.True(vm.IsSingleFile);
        Assert.True(vm.CanShowAnalysis);
        Assert.True(vm.CanShowRaw);
    }

    [Fact]
    public void ManyFiles_HideBothPerFileTabs()
    {
        var vm = Editor();
        vm.LoadMany([new TagTarget(TempFile(), null), new TagTarget(TempFile(), null)]);

        Assert.False(vm.IsSingleFile);
        Assert.False(vm.CanShowAnalysis);
        Assert.False(vm.CanShowRaw);
    }

    /// <summary>The rule itself, walked through every state the editor can be in. This is the one that
    /// would have caught the original bug - each tab's own value looked defensible in isolation.</summary>
    [Fact]
    public void TheTwoPerFileTabs_AgreeInEveryState()
    {
        var vm = Editor();
        var one = TempFile();
        var two = TempFile();

        Assert.Equal(vm.CanShowAnalysis, vm.CanShowRaw);            // nothing selected

        vm.Load(one);
        Assert.Equal(vm.CanShowAnalysis, vm.CanShowRaw);            // one file

        vm.LoadMany([new TagTarget(one, null), new TagTarget(two, null)]);
        Assert.Equal(vm.CanShowAnalysis, vm.CanShowRaw);            // a selection

        vm.LoadMany([new TagTarget(one, null)]);                    // a selection OF one
        Assert.Equal(vm.CanShowAnalysis, vm.CanShowRaw);

        vm.LoadMany([]);                                            // selection emptied
        Assert.Equal(vm.CanShowAnalysis, vm.CanShowRaw);

        vm.Clear();
        Assert.Equal(vm.CanShowAnalysis, vm.CanShowRaw);
    }

    /// <summary>A host that hands over no raw reader has no RAW tab at all - that is the ONE case where
    /// the two legitimately differ, and it is a host's decision rather than a state of the file.</summary>
    [Fact]
    public void WithoutARawReader_OnlyAnalysisIsOffered()
    {
        var vm = new TagEditorViewModel(new FakeTags(), new FakeTags());
        vm.Load(TempFile());

        Assert.True(vm.CanShowAnalysis);
        Assert.False(vm.CanShowRaw);
    }
}
