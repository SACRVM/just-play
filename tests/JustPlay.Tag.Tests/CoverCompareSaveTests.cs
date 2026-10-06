using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.Tests;

/// <summary>
/// A selection's save waits for the background cover pass to let go of the files.
///
/// <para>What was wrong: picking a selection starts a pass that opens every file to compare covers.
/// A save started while it ran wrote into files the pass still had open - and TagLib# opens a file
/// for writing without sharing, so whichever file the pass was standing on failed to save.</para>
/// </summary>
public sealed class CoverCompareSaveTests
{
    /// <summary>The first read - the cover pass's - blocks until released; a write that happens while
    /// any read is still open is recorded.</summary>
    private sealed class SlowStore : IMetadataReader, IMetadataWriter, IDisposable
    {
        public readonly ManualResetEventSlim Gate = new();
        private int _calls, _open, _writes;
        private volatile bool _wroteWhileRead;

        public int Calls => Volatile.Read(ref _calls);
        public int Writes => Volatile.Read(ref _writes);
        public bool WroteWhileRead => _wroteWhileRead;

        public TrackMetadata Read(string filePath)
        {
            Interlocked.Increment(ref _open);
            try
            {
                if (Interlocked.Increment(ref _calls) == 1) Gate.Wait(TimeSpan.FromSeconds(10));
                return Row();
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }

        public EditableTags ReadEditable(string filePath) => EditorialWrite.From(Read(filePath));

        public void WriteEditable(string filePath, EditableTags tags, CoverAction coverAction,
                                  byte[]? newCover, string? coverMimeType)
        {
            if (Volatile.Read(ref _open) > 0) _wroteWhileRead = true;
            Interlocked.Increment(ref _writes);
        }

        public void Write(string filePath, TagWrite write, TagWritePolicy? policy = null) { }

        public void Restore(string filePath, TagRestore restore) { }

        public void ConfigureId3WriteFormat(Id3WriteFormat format) { }

        public void Dispose() => Gate.Dispose();
    }

    private static TrackMetadata Row() => new() { FallbackName = "track", Title = "Old" };

    [Fact]
    public async Task A_save_does_not_write_while_the_cover_pass_holds_a_file()
    {
        using var store = new SlowStore();
        var vm = new TagEditorViewModel(store, store);
        vm.LoadMany([new TagTarget("C:/music/a.mp3", Row()), new TagTarget("C:/music/b.mp3", Row())]);

        // The cover pass is now inside its first read, holding the file.
        Assert.True(SpinWait.SpinUntil(() => store.Calls >= 1, TimeSpan.FromSeconds(5)));

        vm.Title = "New";
        var save = vm.SaveManyAsync();

        // Give a save that does NOT wait every chance to write now; one that waits writes nothing.
        SpinWait.SpinUntil(() => store.Writes > 0, TimeSpan.FromMilliseconds(500));
        store.Gate.Set();
        var report = await save;

        Assert.False(store.WroteWhileRead);
        Assert.Equal(2, report.Written);
    }
}
