using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Tagging;
using JustPlay.UI.ViewModels;
using UiTagField = JustPlay.UI.ViewModels.TagField;

namespace JustPlay.Tag.Tests;

/// <summary>
/// The TRANSFORM write path - read, overlay, write, and above all SKIP.
///
/// <para>Lives in this project because it is the only test project that can see JustPlay.UI, and
/// JUST TAG is the app that ships the feature. The transform itself (what the text becomes) is
/// pinned next door in <c>TextTransformTests</c>; what is pinned here is the part that touches
/// files: that each file gets ITS OWN value transformed, that a file the transform does not change
/// is not written at all, and that a field nobody ticked survives the round trip.</para>
///
/// <para>The last two are the ones that would go unnoticed: an editorial write hands the tagger a
/// COMPLETE set of tags and clears whatever it is not given, so a bug here does not corrupt one
/// field - it empties every field the edit did not mention, across the whole selection.</para>
/// </summary>
public class TagTransformTests
{
    /// <summary>A tag store in memory. Records every write, so "was this file touched at all?" is a
    /// question the test can ask - which is the whole point of the skip.</summary>
    private sealed class FakeTags : IMetadataReader, IMetadataWriter
    {
        public readonly Dictionary<string, TrackMetadata> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Writes = [];

        public TrackMetadata Read(string filePath) =>
            Files.TryGetValue(filePath, out var m) ? m : Blank(filePath);

        private static TrackMetadata Blank(string path) =>
            new() { FallbackName = System.IO.Path.GetFileName(path) };

        public EditableTags ReadEditable(string filePath) => EditorialWrite.From(Read(filePath));

        public void WriteEditable(string filePath, EditableTags tags, CoverAction coverAction,
                                  byte[]? newCover, string? coverMimeType)
        {
            Writes.Add(filePath);
            Files[filePath] = new TrackMetadata
            {
                FallbackName = System.IO.Path.GetFileName(filePath),
                Title = tags.Title,
                Artist = tags.Artist,
                Album = tags.Album,
                AlbumArtist = tags.AlbumArtist,
                Genre = tags.Genre,
                Comment = tags.Comment,
                Year = tags.Year == 0 ? null : tags.Year,
                TrackNumber = tags.TrackNumber == 0 ? null : tags.TrackNumber,
            };
        }

        public void Write(string filePath, TagWrite write, TagWritePolicy? policy = null) =>
            throw new NotSupportedException("a transform is editorial only");

        public void Restore(string filePath, TagRestore restore) =>
            throw new NotSupportedException("a transform is editorial only");

        public void ConfigureId3WriteFormat(Id3WriteFormat format) { }
    }

    private static (FakeTags store, TagTransformViewModel vm) Setup(params TrackMetadata[] files)
    {
        var store = new FakeTags();
        var targets = new List<TagTarget>();

        for (var i = 0; i < files.Length; i++)
        {
            var path = $"C:/music/{i}.mp3";
            store.Files[path] = files[i];
            targets.Add(new TagTarget(path, files[i]));
        }

        return (store, new TagTransformViewModel(store, store, TagEditorViewModel.WriteDirect, targets));
    }

    private static TrackMetadata Track(string? artist = null, string? title = null,
                                       string? genre = null, string? comment = null,
                                       uint? year = null) =>
        new()
        {
            FallbackName = "track.mp3",
            Artist = artist, Title = title, Genre = genre, Comment = comment, Year = year,
        };

    // -- The preview -----------------------------------------------------------------------------

    [Fact]
    public void Only_files_that_change_are_listed()
    {
        var (_, vm) = Setup(Track(artist: "PERC", title: "GOB"),
                            Track(artist: "Ansome", title: "Stowaway"));

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);

        // Two fields of ONE file - the second already reads that way and is absent entirely.
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal("0.mp3", r.File));
        Assert.Contains("1 of 2 files", vm.Summary);
    }

    [Fact]
    public void A_field_nobody_ticked_is_not_previewed()
    {
        var (_, vm) = Setup(Track(artist: "PERC", genre: "HARD TECHNO"));

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);
        Assert.Single(vm.Rows);          // ARTIST is on by default, GENRE is not

        vm.DoGenre = true;
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void Nothing_to_find_is_not_an_instruction()
    {
        var (_, vm) = Setup(Track(artist: "Perc_Gob"));

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.Replace);
        Assert.Empty(vm.Rows);
        Assert.False(vm.CanApply);

        vm.Find = "_";
        Assert.Single(vm.Rows);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public void No_field_ticked_means_nothing_to_apply()
    {
        var (_, vm) = Setup(Track(artist: "PERC", title: "GOB"));
        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);

        vm.DoArtist = false;
        vm.DoTitle = false;

        Assert.Empty(vm.Rows);
        Assert.False(vm.CanApply);
    }

    /// <summary>A replacement that empties a field is allowed - it is what takes a site name out of a
    /// comment - but it is the one outcome that loses a value, so the row says so.</summary>
    [Fact]
    public void A_row_that_empties_a_field_is_flagged()
    {
        var (_, vm) = Setup(Track(comment: "www.somesite.com"));

        vm.DoArtist = false;
        vm.DoTitle = false;
        vm.DoComment = true;
        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.Replace);
        vm.Find = "www.somesite.com";

        Assert.True(vm.Rows.Single().Clears);
        Assert.Contains("EMPTIED", vm.Summary);
    }

    // -- The write -------------------------------------------------------------------------------

    [Fact]
    public async Task Each_file_gets_its_OWN_value_transformed()
    {
        var (store, vm) = Setup(Track(artist: "PERC", title: "GOB"),
                                Track(artist: "ANSOME", title: "STOWAWAY"));

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);
        var report = await vm.ApplyAsync();

        Assert.Equal(2, report.Written);
        Assert.Equal("Perc", store.Files["C:/music/0.mp3"].Artist);
        Assert.Equal("Gob", store.Files["C:/music/0.mp3"].Title);
        Assert.Equal("Ansome", store.Files["C:/music/1.mp3"].Artist);
        Assert.Equal("Stowaway", store.Files["C:/music/1.mp3"].Title);
    }

    /// <summary>The skip. A file the transform does not change must not be opened for writing at all -
    /// no new timestamp, no re-serialised tag over a value it already carried.</summary>
    [Fact]
    public async Task A_file_that_does_not_change_is_never_written()
    {
        var (store, vm) = Setup(Track(artist: "PERC", title: "GOB"),
                                Track(artist: "Ansome", title: "Stowaway"));

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);
        var report = await vm.ApplyAsync();

        Assert.Equal("C:/music/0.mp3", Assert.Single(store.Writes));
        Assert.Equal(1, report.Unchanged);
    }

    /// <summary>An editorial write hands over a COMPLETE set of tags and clears what it is not given,
    /// so every field the transform is not pointed at has to survive by being carried through.</summary>
    [Fact]
    public async Task Fields_the_transform_is_not_pointed_at_survive()
    {
        var (store, vm) = Setup(new TrackMetadata
        {
            FallbackName = "track.mp3",
            Artist = "PERC",
            Title = "Gob",
            Album = "Bulk",
            AlbumArtist = "Various",
            Genre = "Hard Techno",
            Comment = "keep me",
            Year = 2017,
            TrackNumber = 3,
        });

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);
        await vm.ApplyAsync();

        var after = store.Files["C:/music/0.mp3"];
        Assert.Equal("Perc", after.Artist);
        Assert.Equal("Bulk", after.Album);
        Assert.Equal("Various", after.AlbumArtist);
        Assert.Equal("Hard Techno", after.Genre);
        Assert.Equal("keep me", after.Comment);
        Assert.Equal(2017u, after.Year);
        Assert.Equal(3u, after.TrackNumber);
    }

    [Fact]
    public async Task Applying_nothing_writes_nothing()
    {
        var (store, vm) = Setup(Track(artist: "Perc", title: "Gob"));

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);
        Assert.False(vm.CanApply);

        var report = await vm.ApplyAsync();

        Assert.Empty(store.Writes);
        Assert.Equal(0, report.Written);
    }

    /// <summary>Every write goes through the host's executor, so a host that DEFERS one (JUST PLAY,
    /// when the file is the playing track) is reported as deferred rather than as written.</summary>
    [Fact]
    public async Task The_write_goes_through_the_hosts_executor()
    {
        var store = new FakeTags();
        const string path = "C:/music/playing.mp3";
        store.Files[path] = Track(artist: "PERC");

        var seen = new List<string>();
        var vm = new TagTransformViewModel(store, store,
            (p, _) => { seen.Add(p); return TagWriteOutcome.Deferred; },
            [new TagTarget(path, store.Files[path])]);

        vm.Operation = vm.Operations.First(o => o.Value == TextOperation.TitleCase);
        var report = await vm.ApplyAsync();

        Assert.Equal(path, Assert.Single(seen));
        Assert.Equal(1, report.Deferred);
        Assert.Empty(store.Writes);       // the executor never ran the write
    }

    // -- The shared overlay ----------------------------------------------------------------------

    [Fact]
    public void Changes_is_blind_to_null_versus_empty()
    {
        var current = new TrackMetadata { FallbackName = "t.mp3", Artist = "Perc", Comment = "" };
        var next = EditorialWrite.From(current);

        Assert.False(EditorialWrite.Changes(next, current));
        Assert.False(EditorialWrite.Changes(next with { Comment = null }, current));
        Assert.True(EditorialWrite.Changes(next with { Artist = "Ansome" }, current));
    }

    [Fact]
    public void Over_only_touches_the_text_fields()
    {
        var current = new TrackMetadata
        {
            FallbackName = "t.mp3", Artist = "perc", Year = 2017, TrackNumber = 3,
        };
        var next = EditorialWrite.Over(current,
                                       (f, v) => f == UiTagField.Artist ? v?.ToUpperInvariant() : v);

        Assert.Equal("PERC", next.Artist);
        Assert.Equal(2017u, next.Year);
        Assert.Equal(3u, next.TrackNumber);
    }
}
