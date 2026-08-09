using JustPlay.Core.Organise;

namespace JustPlay.Core.Tests;

/// <summary>
/// The remembered destinations. <see cref="RecentDestinations.Location"/> is redirected at a temp
/// file for the whole class, exactly as <c>LibraryIndexRegistryTests</c> redirects the root
/// registry, so a test run can never write into the real machine's list.
/// </summary>
public sealed class RecentDestinationsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _saved = RecentDestinations.Location;

    public RecentDestinationsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jp-recent-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        RecentDestinations.Location = Path.Combine(_dir, "recent.json");
    }

    public void Dispose()
    {
        RecentDestinations.Location = _saved;
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* temp */ }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Nothing_remembered_reads_as_an_empty_list_not_an_error()
    {
        Assert.Empty(RecentDestinations.All());
    }

    [Fact]
    public void The_most_recent_is_first()
    {
        var a = Folder("a");
        var b = Folder("b");

        RecentDestinations.Remember(a);
        RecentDestinations.Remember(b);

        Assert.Equal([b, a], RecentDestinations.All());
    }

    [Fact]
    public void Remembering_the_same_folder_again_moves_it_up_rather_than_repeating_it()
    {
        var a = Folder("a");
        var b = Folder("b");

        RecentDestinations.Remember(a);
        RecentDestinations.Remember(b);
        RecentDestinations.Remember(a);

        Assert.Equal([a, b], RecentDestinations.All());
    }

    [Fact]
    public void Only_the_last_few_are_kept()
    {
        for (var i = 0; i < RecentDestinations.Keep + 4; i++)
            RecentDestinations.Remember(Folder("f" + i));

        Assert.Equal(RecentDestinations.Keep, RecentDestinations.All().Count);
    }

    [Fact]
    public void A_folder_that_is_no_longer_there_is_not_offered()
    {
        var gone = Folder("gone");
        RecentDestinations.Remember(gone);
        Directory.Delete(gone);

        Assert.Empty(RecentDestinations.All());
    }

    [Fact]
    public void A_corrupt_file_reads_as_nothing_remembered()
    {
        File.WriteAllText(RecentDestinations.Location, "{ this is not json");

        Assert.Empty(RecentDestinations.All());
    }

    [Fact]
    public void Blank_input_is_ignored()
    {
        RecentDestinations.Remember(null);
        RecentDestinations.Remember("   ");

        Assert.Empty(RecentDestinations.All());
    }
}
