using System;
using System.Collections.Generic;
using System.IO;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// "Is the editor holding any of these files?" - the question behind two rules of an ANALYZE run:
/// the editor takes no edits to a file the batch is writing, and afterwards it re-reads only what
/// it is holding.
///
/// <para>What was wrong: after a batch the editor was re-pointed at the analysed rows whether it
/// still held them or not. If the selection had moved on, the editor ended up on the analysed set
/// while the list showed another one, and edits typed meanwhile were thrown away unasked. And a
/// hand correction saved during the batch wrote the pre-analysis blob back over the fresh one.</para>
/// </summary>
public class EditorHoldsAnyTests : IDisposable
{
    private sealed class FakeTags : IMetadataReader, IMetadataWriter
    {
        public TrackMetadata Read(string filePath) =>
            new() { FallbackName = Path.GetFileName(filePath) };

        public EditableTags ReadEditable(string filePath) => EditorialWrite.From(Read(filePath));

        public void WriteEditable(string filePath, EditableTags tags, EditableField fields,
                                  CoverAction coverAction, byte[]? newCover, string? coverMimeType) { }

        public void Write(string filePath, TagWrite write, TagWritePolicy? policy = null) { }

        public void Restore(string filePath, TagRestore restore) { }

        public void ConfigureId3WriteFormat(Id3WriteFormat format) { }
    }

    private readonly List<string> _temp = [];

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"justtag_holds_{Guid.NewGuid():N}.mp3");
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

    private static TagEditorViewModel Editor() => new(new FakeTags(), new FakeTags());

    private static HashSet<string> Batch(params string[] paths) =>
        new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void An_empty_editor_holds_nothing()
    {
        Assert.False(TaggerViewModel.EditorHoldsAny(Editor(), Batch(TempFile())));
    }

    [Fact]
    public void One_file_in_the_batch_is_held()
    {
        var a = TempFile();
        var editor = Editor();
        editor.Load(a);

        Assert.True(TaggerViewModel.EditorHoldsAny(editor, Batch(TempFile(), a)));
    }

    /// <summary>The selection moved on while the batch ran: the editor is showing a file the batch
    /// never touched, and it must be left exactly as it is.</summary>
    [Fact]
    public void One_file_outside_the_batch_is_not_held()
    {
        var editor = Editor();
        editor.Load(TempFile());

        Assert.False(TaggerViewModel.EditorHoldsAny(editor, Batch(TempFile(), TempFile())));
    }

    /// <summary>Windows paths: the same file in a different case is the same file.</summary>
    [Fact]
    public void A_path_that_differs_only_in_case_is_the_same_file()
    {
        var a = TempFile();
        var editor = Editor();
        editor.Load(a);

        Assert.True(TaggerViewModel.EditorHoldsAny(editor, Batch(a.ToUpperInvariant())));
    }

    /// <summary>A selection that shares even ONE file with the batch holds something the batch
    /// writes - the whole selection is stale in that file.</summary>
    [Fact]
    public void A_selection_overlapping_the_batch_is_held()
    {
        var a = TempFile();
        var editor = Editor();
        editor.LoadMany([new TagTarget(TempFile(), null), new TagTarget(a, null)]);

        Assert.True(TaggerViewModel.EditorHoldsAny(editor, Batch(a, TempFile())));
    }

    [Fact]
    public void A_selection_apart_from_the_batch_is_not_held()
    {
        var editor = Editor();
        editor.LoadMany([new TagTarget(TempFile(), null), new TagTarget(TempFile(), null)]);

        Assert.False(TaggerViewModel.EditorHoldsAny(editor, Batch(TempFile(), TempFile())));
    }

    [Fact]
    public void No_batch_holds_nothing()
    {
        var editor = Editor();
        editor.Load(TempFile());

        Assert.False(TaggerViewModel.EditorHoldsAny(editor, Batch()));
    }
}
