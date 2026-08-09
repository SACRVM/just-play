using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Library;
using Xunit;

namespace JustPlay.Library.Tests;

/// <summary>
/// Never leave a song behind - the index half of move / copy / delete.
///
/// <para>A real temp library with a real database, because the whole point is that the index really
/// changes. Both static seams are redirected for the class:
/// <see cref="LibraryIndexRegistry.Location"/> at a temp registry, and the database file itself is
/// derived from the temp root's hash and removed afterwards, so a run can neither register a root
/// into the real machine nor leave an index behind.</para>
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class LibraryOrganiseSyncTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jp-orgsync-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _registryDir;
    private readonly string _savedRegistry = LibraryIndexRegistry.Location;
    private readonly FakeReader _tags = new();

    private readonly string _from;
    private readonly string _to;

    public LibraryOrganiseSyncTests()
    {
        _from = Path.Combine(_root, "from");
        _to = Path.Combine(_root, "to");
        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_to);

        _registryDir = Path.Combine(Path.GetTempPath(), "jp-orgsync-reg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_registryDir);
        LibraryIndexRegistry.Location = Path.Combine(_registryDir, "roots.json");
    }

    public void Dispose()
    {
        LibraryIndexRegistry.Location = _savedRegistry;

        // The index file lives under the suite data folder, keyed by a hash of the temp root - so it
        // is ours alone, and it goes with us.
        var db = LibraryDb.DefaultPathFor(_root);
        foreach (var path in new[] { db, db + "-wal", db + "-shm" })
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_registryDir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeReader : IMetadataReader
    {
        public TrackMetadata Read(string filePath) =>
            new() { FallbackName = Path.GetFileNameWithoutExtension(filePath) };

        public EditableTags ReadEditable(string filePath) => new();
    }

    private static string Write(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "not really audio, but it has a size and a date");
        return path;
    }

    private IReadOnlyList<string> Indexed(bool includeMissing)
    {
        using var db = LibraryDb.OpenForRoot(_root);
        return [.. db.Query(new LibraryQuery
        {
            PathPrefix     = _root,
            Recursive      = true,
            SuccessOnly    = false,
            IncludeMissing = includeMissing,
        }).Select(e => e.FilePath)];
    }

    /// <summary>Index the file so there is something for the move to disturb.</summary>
    private void Index(params string[] paths)
    {
        using var db = LibraryDb.OpenForRoot(_root);
        new LibrarySync(db, _tags).VerifyTracks(paths);
    }

    // -- The rule --------------------------------------------------------------------------------

    [Fact]
    public void A_moved_track_is_found_at_its_new_path_and_flagged_at_the_old_one()
    {
        LibraryIndexRegistry.Register(_root);
        var before = Write(_from, "a.mp3");
        Index(before);

        var after = Path.Combine(_to, "a.mp3");
        File.Move(before, after);

        new LibraryOrganiseSync(_tags).PathsChanged([after], [before]);

        var live = Indexed(includeMissing: false);
        Assert.Contains(after, live);
        Assert.DoesNotContain(before, live);

        // (!!) Flagged, not deleted. A row that is simply somewhere else must still be findable -
        // that is the difference between "moved" and "lost".
        Assert.Contains(before, Indexed(includeMissing: true));
    }

    [Fact]
    public void A_copy_adds_the_arrival_and_leaves_the_original_alone()
    {
        LibraryIndexRegistry.Register(_root);
        var original = Write(_from, "a.mp3");
        Index(original);

        var copy = Path.Combine(_to, "a.mp3");
        File.Copy(original, copy);

        new LibraryOrganiseSync(_tags).PathsChanged([copy], []);

        var live = Indexed(includeMissing: false);
        Assert.Contains(original, live);
        Assert.Contains(copy, live);
    }

    [Fact]
    public void A_deleted_track_is_flagged_missing()
    {
        LibraryIndexRegistry.Register(_root);
        var path = Write(_from, "a.mp3");
        Index(path);

        File.Delete(path);   // the recycle bin's effect, without the recycle bin

        new LibraryOrganiseSync(_tags).PathsChanged([], [path]);

        Assert.DoesNotContain(path, Indexed(includeMissing: false));
        Assert.Contains(path, Indexed(includeMissing: true));
    }

    [Fact]
    public void The_folder_fingerprints_of_both_ends_are_forgotten()
    {
        // The fingerprint is a (count, newest mtime) early-out a later VerifyFolder trusts. Left in
        // place after a move it would let the next folder check short-circuit straight past it.
        LibraryIndexRegistry.Register(_root);
        var before = Write(_from, "a.mp3");
        Index(before);

        using (var db = LibraryDb.OpenForRoot(_root))
        {
            db.SetFolderState(_from,
                FolderFingerprint.FromFiles([.. AudioFiles.EnumerateWithKeys(_from, false)]));
            db.SetFolderState(_to,
                FolderFingerprint.FromFiles([.. AudioFiles.EnumerateWithKeys(_to, false)]));
        }

        var after = Path.Combine(_to, "a.mp3");
        File.Move(before, after);

        new LibraryOrganiseSync(_tags).PathsChanged([after], [before]);

        using var check = LibraryDb.OpenForRoot(_root);
        Assert.Null(check.FolderState(_from));
        Assert.Null(check.FolderState(_to));
    }

    // -- The guard -------------------------------------------------------------------------------

    [Fact]
    public void An_unindexed_folder_does_not_get_a_database_conjured_for_it()
    {
        // (!!) LibraryDb.Open is ReadWriteCreate. Reaching for an index that was never built would
        // leave an empty one behind that reads as "indexed" forever - so the registry is asked
        // first, and this root was never registered.
        var moved = Write(_to, "a.mp3");

        new LibraryOrganiseSync(_tags).PathsChanged([moved], [Path.Combine(_from, "a.mp3")]);

        Assert.False(File.Exists(LibraryDb.DefaultPathFor(_root)));
    }

    [Fact]
    public void A_registered_root_that_was_never_scanned_is_left_alone()
    {
        // Registered but with no database file yet - OpenFor returns null rather than creating one.
        LibraryIndexRegistry.Register(_root);
        var moved = Write(_to, "a.mp3");

        new LibraryOrganiseSync(_tags).PathsChanged([moved], []);

        Assert.False(File.Exists(LibraryDb.DefaultPathFor(_root)));
    }

    [Fact]
    public void Nothing_touched_is_a_no_op()
    {
        LibraryIndexRegistry.Register(_root);

        new LibraryOrganiseSync(_tags).PathsChanged([], []);

        Assert.False(File.Exists(LibraryDb.DefaultPathFor(_root)));
    }

    [Fact]
    public void A_path_that_makes_no_sense_is_ignored_rather_than_thrown()
    {
        // By the time this runs the files have already moved. An index that cannot be reached is a
        // browser that is briefly out of date, never a failed move.
        var record = new List<string>();
        var sync = new LibraryOrganiseSync(_tags, record.Add);

        sync.PathsChanged(["", "   ", "not a path at all"], []);

        Assert.Empty(record);
    }
}
