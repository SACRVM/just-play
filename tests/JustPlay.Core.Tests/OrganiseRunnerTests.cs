using System.Security.Cryptography;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Organise;

namespace JustPlay.Core.Tests;

/// <summary>
/// The half that actually writes. Integration level by necessity - the point of these is that the
/// bytes really land and the source really goes - so they use a temp directory they create and
/// remove, never a real music folder.
///
/// <para>The recycle bin stays a fake: a test that filled the machine's actual Recycle Bin would be
/// a test that leaves litter behind, and the platform call is exercised by the app, not here.</para>
/// </summary>
public sealed class OrganiseRunnerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jp-organise-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _from;
    private readonly string _to;

    public OrganiseRunnerTests()
    {
        _from = Path.Combine(_root, "from");
        _to = Path.Combine(_root, "to");
        Directory.CreateDirectory(_from);
        Directory.CreateDirectory(_to);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // -- Fixtures --------------------------------------------------------------------------------

    /// <summary>A fake bin that only records. It deletes nothing, so a failed assertion cannot cost
    /// anything, and "was it recycled" is a list rather than a trip to the shell.</summary>
    private sealed class FakeBin(bool available = true) : IRecycleBin
    {
        public readonly List<string> Sent = [];
        public string? RefuseThis;

        public bool IsAvailable => available;
        public string Name => "Recycle Bin";

        public void Send(string path)
        {
            if (string.Equals(path, RefuseThis, StringComparison.OrdinalIgnoreCase))
                throw new IOException("this one is held open");

            Sent.Add(path);
            File.Delete(path);   // the real bin makes the file go; so must the fake
        }
    }

    private string Write(string folder, string name, int bytes = 4096)
    {
        var path = Path.Combine(folder, name);
        var data = new byte[bytes];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private OrganisePlan Plan(OrganiseAction action, string? destination, params string[] sources) =>
        OrganisePlanner.Plan(new OrganiseRequest
        {
            Action              = action,
            Sources             = sources,
            Destination         = destination,
            RecycleBinAvailable = true,
        }, SystemFileProbe.Instance);

    // -- Copy ------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_puts_the_bytes_there_and_leaves_the_original()
    {
        var source = Write(_from, "a.mp3", 200_000);
        var before = Sha(source);

        var report = await new OrganiseRunner(new FakeBin())
            .RunAsync(Plan(OrganiseAction.Copy, _to, source));

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.True(File.Exists(source));
        Assert.Equal(before, Sha(Path.Combine(_to, "a.mp3")));
        Assert.Empty(report.RemovedPaths);
        Assert.Single(report.AddedPaths);
    }

    [Fact]
    public async Task A_copy_leaves_no_partial_file_behind()
    {
        var source = Write(_from, "a.mp3");

        await new OrganiseRunner(new FakeBin()).RunAsync(Plan(OrganiseAction.Copy, _to, source));

        Assert.Empty(Directory.GetFiles(_to, ".just-part-*"));
    }

    // -- Move ------------------------------------------------------------------------------------

    [Fact]
    public async Task Move_within_one_volume_takes_the_file_with_it()
    {
        var source = Write(_from, "a.mp3", 50_000);
        var before = Sha(source);

        var report = await new OrganiseRunner(new FakeBin())
            .RunAsync(Plan(OrganiseAction.Move, _to, source));

        Assert.Equal(1, report.Succeeded);
        Assert.False(File.Exists(source));
        Assert.Equal(before, Sha(Path.Combine(_to, "a.mp3")));
        Assert.Equal(source, Assert.Single(report.RemovedPaths));
        Assert.Equal(Path.Combine(_to, "a.mp3"), Assert.Single(report.AddedPaths));
    }

    [Fact]
    public async Task A_verified_cross_volume_move_copies_checks_then_removes()
    {
        // The volume heuristic says "same drive" for a temp folder, so the verified path is driven
        // directly - the branch is the same code either way, and this is where the check lives.
        var source = Write(_from, "a.mp3", 300_000);
        var before = Sha(source);

        var item = new OrganiseItem
        {
            SourcePath      = source,
            DestinationPath = Path.Combine(_to, "a.mp3"),
            Status          = OrganiseItemStatus.Ready,
            Bytes           = new FileInfo(source).Length,
            CrossVolume     = true,
        };

        var report = await new OrganiseRunner(new FakeBin()).RunAsync(new OrganisePlan
        {
            Action = OrganiseAction.Move,
            Items  = [item],
        });

        Assert.Equal(1, report.Succeeded);
        Assert.False(File.Exists(source));
        Assert.Equal(before, Sha(Path.Combine(_to, "a.mp3")));
    }

    [Fact]
    public async Task A_source_that_cannot_be_read_is_reported_and_left_alone()
    {
        var good = Write(_from, "good.mp3");
        var locked = Write(_from, "locked.mp3");

        // An exclusive handle is exactly what a preview or another app holds.
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var report = await new OrganiseRunner(new FakeBin())
            .RunAsync(Plan(OrganiseAction.Move, _to, locked, good));

        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(locked, Assert.Single(report.Failures).Path);
        Assert.True(File.Exists(locked));                        // untouched
        Assert.False(File.Exists(good));                         // the batch kept going
        Assert.Empty(Directory.GetFiles(_to, ".just-part-*"));   // and cleaned up after itself
    }

    // -- Collisions ------------------------------------------------------------------------------

    [Fact]
    public async Task A_collision_is_skipped_and_the_file_that_is_there_is_untouched()
    {
        var source = Write(_from, "a.mp3");
        var existing = Write(_to, "a.mp3");
        var existingBefore = Sha(existing);

        var report = await new OrganiseRunner(new FakeBin())
            .RunAsync(Plan(OrganiseAction.Move, _to, source));

        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Skipped);
        Assert.True(File.Exists(source));
        Assert.Equal(existingBefore, Sha(existing));   // (!!) never overwritten
    }

    [Fact]
    public async Task Keep_both_writes_the_suffixed_name_and_still_does_not_overwrite()
    {
        var source = Write(_from, "a.mp3");
        var sourceBefore = Sha(source);
        var existing = Write(_to, "a.mp3");
        var existingBefore = Sha(existing);

        var plan = OrganisePlanner.Plan(new OrganiseRequest
        {
            Action      = OrganiseAction.Move,
            Sources     = [source],
            Destination = _to,
            Collisions  = CollisionPolicy.KeepBoth,
        }, SystemFileProbe.Instance);

        var report = await new OrganiseRunner(new FakeBin()).RunAsync(plan);

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(existingBefore, Sha(existing));
        Assert.Equal(sourceBefore, Sha(Path.Combine(_to, "a (2).mp3")));
    }

    // -- Delete ----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_goes_to_the_bin()
    {
        var source = Write(_from, "a.mp3");
        var bin = new FakeBin();

        var report = await new OrganiseRunner(bin).RunAsync(Plan(OrganiseAction.Delete, null, source));

        Assert.Equal(1, report.Succeeded);
        Assert.Equal(source, Assert.Single(bin.Sent));
        Assert.Equal(source, Assert.Single(report.RemovedPaths));
        Assert.Empty(report.AddedPaths);
    }

    [Fact]
    public async Task Delete_refuses_rather_than_deleting_for_good_when_there_is_no_bin()
    {
        // (!!) The rule the feature turns on. The plan blocks it, and even a plan that somehow got
        // through has to fail at the runner rather than fall back to File.Delete.
        var source = Write(_from, "a.mp3");

        var report = await new OrganiseRunner(new FakeBin(available: false)).RunAsync(new OrganisePlan
        {
            Action = OrganiseAction.Delete,
            Items  = [new OrganiseItem { SourcePath = source, Status = OrganiseItemStatus.Ready }],
        });

        Assert.Equal(1, report.Failed);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_file_the_bin_refuses_is_reported_and_stays()
    {
        var stubborn = Write(_from, "stubborn.mp3");
        var fine = Write(_from, "fine.mp3");
        var bin = new FakeBin { RefuseThis = stubborn };

        var report = await new OrganiseRunner(bin)
            .RunAsync(Plan(OrganiseAction.Delete, null, stubborn, fine));

        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.Succeeded);
        Assert.True(File.Exists(stubborn));
        Assert.False(File.Exists(fine));
    }

    // -- Progress and cancellation ---------------------------------------------------------------

    [Fact]
    public async Task Progress_reports_the_files_and_the_bytes()
    {
        var a = Write(_from, "a.mp3", 100_000);
        var b = Write(_from, "b.mp3", 100_000);

        var seen = new List<OrganiseProgress>();
        var progress = new Progress<OrganiseProgress>(seen.Add);

        var report = await new OrganiseRunner(new FakeBin())
            .RunAsync(Plan(OrganiseAction.Copy, _to, a, b), progress);

        Assert.Equal(2, report.Succeeded);

        // Progress<T> posts, so give the synchronisation context its turn before reading.
        await Task.Yield();
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(2, p.Total));
        Assert.Equal(200_000, seen[^1].BytesTotal);
    }

    [Fact]
    public async Task A_cancelled_run_reports_instead_of_throwing_and_keeps_what_landed()
    {
        var a = Write(_from, "a.mp3");
        var b = Write(_from, "b.mp3");
        var c = Write(_from, "c.mp3");

        using var cts = new CancellationTokenSource();
        var plan = Plan(OrganiseAction.Move, _to, a, b, c);

        // Stop as soon as the first file is done.
        var progress = new Progress<OrganiseProgress>(p => { if (p.Done >= 1) cts.Cancel(); });

        var report = await new OrganiseRunner(new FakeBin()).RunAsync(plan, progress, cts.Token);

        Assert.True(report.Cancelled);
        Assert.True(report.Succeeded >= 1);
        Assert.True(report.Succeeded < 3);
        Assert.Empty(Directory.GetFiles(_to, ".just-part-*"));

        // Whatever did NOT move is still exactly where it was - a cancelled move is not a rollback,
        // it is a stop.
        var moved = report.RemovedPaths.Count;
        Assert.Equal(3 - moved, Directory.GetFiles(_from, "*.mp3").Length);
    }

    [Fact]
    public async Task A_failure_after_the_bytes_landed_still_reports_the_new_file()
    {
        // The move that fails at the last step: the copy is verified and in place, and the source
        // will not go. Both files exist, and the index has to hear about the one that arrived - a
        // track that exists and is in no index is a track she cannot find.
        var source = Write(_from, "a.mp3");
        var landed = Path.Combine(_to, "a.mp3");
        File.Copy(source, landed);   // stand in for a copy that already succeeded

        var report = await new OrganiseRunner(new FakeBin()).RunAsync(new OrganisePlan
        {
            Action = OrganiseAction.Move,
            Items  =
            [
                new OrganiseItem
                {
                    // The destination is taken, so File.Move throws - after "the bytes are there".
                    SourcePath      = source,
                    DestinationPath = landed,
                    Status          = OrganiseItemStatus.Ready,
                },
            ],
        });

        Assert.Equal(1, report.Failed);
        Assert.Contains(landed, report.AddedPaths);
        Assert.Empty(report.RemovedPaths);      // the source is still there, so nothing departed
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task An_empty_plan_does_nothing_and_says_so()
    {
        var report = await new OrganiseRunner(new FakeBin())
            .RunAsync(OrganisePlan.Empty(OrganiseAction.Move));

        Assert.Equal(0, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.False(report.Cancelled);
    }

    [Fact]
    public async Task The_report_reads_as_a_sentence()
    {
        var source = Write(_from, "a.mp3");

        var report = await new OrganiseRunner(new FakeBin())
            .RunAsync(Plan(OrganiseAction.Copy, _to, source));

        Assert.Contains("copied", report.ToString(), StringComparison.Ordinal);
    }

    // -- The write probe -------------------------------------------------------------------------

    [Fact]
    public void The_write_probe_leaves_nothing_behind()
    {
        Assert.True(SystemFileProbe.Instance.CanWriteTo(_to));
        Assert.Empty(Directory.GetFiles(_to));
    }

    [Fact]
    public void A_folder_that_is_not_there_cannot_be_written_to()
    {
        Assert.False(SystemFileProbe.Instance.CanWriteTo(Path.Combine(_root, "nope")));
    }
}
