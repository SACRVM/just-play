using System;
using System.Collections.Generic;
using System.IO;
using JustPlay.Library;
using Xunit;

namespace JustPlay.Library.Tests;

/// <summary>
/// The machine-level "which folders are indexed" registry. Two halves are tested separately on purpose:
/// the CONTAINMENT rules are pure and get the edge cases, and the file round-trip runs against a temp
/// path - <see cref="LibraryIndexRegistry.Location"/> is redirected in every test that writes, so a test
/// run can never register a root into the real machine's registry.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class LibraryIndexRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _saved = LibraryIndexRegistry.Location;

    public LibraryIndexRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "justplay-registry-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        LibraryIndexRegistry.Location = Path.Combine(_dir, "roots.json");
    }

    public void Dispose()
    {
        LibraryIndexRegistry.Location = _saved;
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* temp */ }
    }

    private static string Abs(params string[] parts) =>
        Path.GetFullPath(Path.Combine([Path.GetTempPath(), .. parts]));

    // -- Containment (pure) ----------------------------------------------------------------------

    [Fact]
    public void The_root_itself_counts_as_inside_it()
    {
        var root = Abs("music");
        Assert.Equal(root, LibraryIndexRegistry.RootFor([root], root));
    }

    [Fact]
    public void A_subfolder_resolves_to_its_root()
    {
        var root = Abs("music");
        Assert.Equal(root, LibraryIndexRegistry.RootFor([root], Abs("music", "GENRES", "hardtechno")));
    }

    /// <summary>The bug a naive StartsWith would have: two sibling folders where one name is a prefix
    /// of the other. "...\music2" is NOT inside "...\music".</summary>
    [Fact]
    public void A_sibling_whose_name_merely_starts_the_same_is_outside()
    {
        Assert.Null(LibraryIndexRegistry.RootFor([Abs("music")], Abs("music2")));
    }

    [Fact]
    public void A_folder_outside_every_root_is_unindexed()
    {
        Assert.Null(LibraryIndexRegistry.RootFor([Abs("music")], Abs("Downloads", "new stuff")));
    }

    /// <summary>Nested roots: the MORE SPECIFIC index wins, not whichever was registered first.</summary>
    [Fact]
    public void The_longest_matching_root_wins()
    {
        var outer = Abs("music");
        var inner = Abs("music", "GENRES");

        Assert.Equal(inner, LibraryIndexRegistry.RootFor([outer, inner], Abs("music", "GENRES", "techno")));
        Assert.Equal(outer, LibraryIndexRegistry.RootFor([outer, inner], Abs("music", "SETS")));
    }

    [Fact]
    public void Matching_ignores_case_and_a_trailing_separator()
    {
        var root = Abs("Music");
        Assert.Equal(Path.GetFullPath(root),
                     LibraryIndexRegistry.RootFor([root + Path.DirectorySeparatorChar], Abs("MUSIC", "genres")));
    }

    [Fact]
    public void Nothing_registered_means_nothing_is_indexed()
    {
        Assert.Null(LibraryIndexRegistry.RootFor(new List<string>(), Abs("music")));
        Assert.Null(LibraryIndexRegistry.RootFor([Abs("music")], null));
    }

    // -- The file --------------------------------------------------------------------------------

    [Fact]
    public void A_missing_registry_file_reads_as_empty_not_as_an_error()
    {
        Assert.Empty(LibraryIndexRegistry.Roots());
        Assert.Null(LibraryIndexRegistry.RootFor(Abs("music")));
    }

    [Fact]
    public void A_registered_root_survives_a_round_trip()
    {
        var root = Abs("music");
        LibraryIndexRegistry.Register(root);

        Assert.Equal([root], LibraryIndexRegistry.Roots());
        Assert.Equal(root, LibraryIndexRegistry.RootFor(Abs("music", "GENRES")));
    }

    [Fact]
    public void Registering_twice_does_not_duplicate_it()
    {
        var root = Abs("music");
        LibraryIndexRegistry.Register(root);
        LibraryIndexRegistry.Register(root + Path.DirectorySeparatorChar);
        LibraryIndexRegistry.Register(root.ToUpperInvariant());

        Assert.Single(LibraryIndexRegistry.Roots());
    }

    [Fact]
    public void Unregister_removes_exactly_one_root()
    {
        var a = Abs("music");
        var b = Abs("other");
        LibraryIndexRegistry.Register(a);
        LibraryIndexRegistry.Register(b);

        LibraryIndexRegistry.Unregister(a);

        Assert.Equal([b], LibraryIndexRegistry.Roots());
    }

    [Fact]
    public void A_corrupt_registry_file_reads_as_nothing_indexed()
    {
        File.WriteAllText(LibraryIndexRegistry.Location, "{ this is not json");
        Assert.Empty(LibraryIndexRegistry.Roots());
    }

    /// <summary>
    /// The one that protects her disk: asking for an index must never CREATE one. LibraryDb.Open is
    /// ReadWriteCreate, so an unguarded call would leave an empty database behind that then reads as
    /// "this root is indexed" forever.
    /// </summary>
    [Fact]
    public void OpenFor_does_not_create_a_database_for_a_registered_but_unscanned_root()
    {
        var root = Path.Combine(_dir, "music");
        Directory.CreateDirectory(root);
        LibraryIndexRegistry.Register(root);

        var db = LibraryIndexRegistry.OpenFor(root);

        Assert.Null(db);
        Assert.False(File.Exists(LibraryDb.DefaultPathFor(root)));
    }

    [Fact]
    public void OpenFor_a_folder_outside_every_root_is_null()
    {
        LibraryIndexRegistry.Register(Path.Combine(_dir, "music"));
        Assert.Null(LibraryIndexRegistry.OpenFor(Path.Combine(_dir, "elsewhere")));
    }
}
