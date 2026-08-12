using JustPlay.Core.Abstractions;
using JustPlay.Core.Organise;

namespace JustPlay.Core.Tests;

/// <summary>
/// The PURE half of move / copy / delete. Not one of these touches a real disk: the planner asks an
/// <see cref="IFileProbe"/>, and the fake below IS the filesystem - which is what makes "the name is
/// already taken twice over", "the file vanished while the menu was open" and "this move has to
/// cross a volume" one table-driven test each rather than a temp directory and a prayer.
/// </summary>
public sealed class OrganisePlannerTests
{
    private const string Src = @"C:\music\inbox";
    private const string SrcShouting = @"C:\MUSIC\INBOX";
    private const string Dst = @"C:\music\GENRES\Hard Techno";

    // -- The fake disk ---------------------------------------------------------------------------

    private sealed class FakeDisk : IFileProbe
    {
        public readonly Dictionary<string, long> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Path prefix -> volume token. Everything else falls back to the drive letter.</summary>
        public readonly Dictionary<string, string> Volumes = new(StringComparer.OrdinalIgnoreCase);

        public FakeDisk WithFile(string path, long bytes = 1024)
        {
            Files[path] = bytes;
            Folders.Add(Path.GetDirectoryName(path)!);
            return this;
        }

        public FakeDisk WithFolder(string path) { Folders.Add(path); return this; }

        public bool FileExists(string path) => Files.ContainsKey(path);

        public bool DirectoryExists(string path) => Folders.Contains(path);

        public long SizeOf(string path) => Files.GetValueOrDefault(path);

        public string? VolumeOf(string path)
        {
            foreach (var (prefix, volume) in Volumes)
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return volume;

            return Path.GetPathRoot(path)?.TrimEnd('\\', '/').ToUpperInvariant();
        }

        public bool CanWriteTo(string folder) => Folders.Contains(folder) && !ReadOnly.Contains(folder);
    }

    private static OrganisePlan Plan(
        FakeDisk disk, OrganiseAction action, IReadOnlyList<string> sources,
        string? destination = Dst, CollisionPolicy policy = CollisionPolicy.Skip,
        bool bin = true) =>
        OrganisePlanner.Plan(new OrganiseRequest
        {
            Action              = action,
            Sources             = sources,
            Destination         = action == OrganiseAction.Delete ? null : destination,
            Collisions          = policy,
            RecycleBinAvailable = bin,
        }, disk);

    // -- A drive root as the destination -----------------------------------------------------------

    /// <summary>
    /// A USB stick picked as "E:\" must land files IN the drive, not in whatever folder the process
    /// happens to be sitting in on that drive.
    ///
    /// <para>The bug this pins: normalising trimmed the trailing separator off everything, and "D:\"
    /// trimmed to "D:" is a DRIVE-RELATIVE path. <c>Path.Combine("D:", "a.mp3")</c> is "D:a.mp3",
    /// which Windows resolves against the current directory of drive D - measured as
    /// D:\repos\just-play\a.mp3 with the app started from there. A COPY would have put the file in
    /// the wrong folder; a MOVE would have taken it out of the library to get it there.</para>
    /// </summary>
    [Theory]
    [InlineData(@"E:\")]
    [InlineData("E:")]
    public void A_drive_root_destination_stays_rooted(string picked)
    {
        var disk = new FakeDisk()
            .WithFile(@"E:\music\a.mp3", 100)
            .WithFolder(@"E:\");

        var plan = Plan(disk, OrganiseAction.Copy, [@"E:\music\a.mp3"], destination: picked);

        Assert.Equal(@"E:\", plan.Destination);
        Assert.Equal(@"E:\a.mp3", plan.Items[0].DestinationPath);

        // The real test of "rooted": the path means the same thing from anywhere.
        Assert.Equal(plan.Items[0].DestinationPath,
                     Path.GetFullPath(plan.Items[0].DestinationPath!));
    }

    /// <summary>A share root has no drive-relative form, so it keeps behaving as it always did - the
    /// fix above is a drive-letter case and must not start rewriting UNC paths.</summary>
    [Fact]
    public void A_share_root_destination_is_left_alone()
    {
        var disk = new FakeDisk()
            .WithFile(@"\\nas\music\a.mp3", 100)
            .WithFolder(@"\\nas\other");

        var plan = Plan(disk, OrganiseAction.Copy, [@"\\nas\music\a.mp3"], destination: @"\\nas\other\");

        Assert.Equal(@"\\nas\other", plan.Destination);
        Assert.Equal(@"\\nas\other\a.mp3", plan.Items[0].DestinationPath);
    }

    // -- The happy path --------------------------------------------------------------------------

    [Fact]
    public void Copy_plans_one_item_per_file_with_sizes_and_a_destination()
    {
        var disk = new FakeDisk()
            .WithFile($@"{Src}\a.mp3", 100)
            .WithFile($@"{Src}\b.mp3", 250)
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3", $@"{Src}\b.mp3"]);

        Assert.True(plan.CanRun);
        Assert.Equal(2, plan.RunCount);
        Assert.Equal(0, plan.SkipCount);
        Assert.Equal(350, plan.TotalBytes);
        Assert.Equal($@"{Dst}\a.mp3", plan.Items[0].DestinationPath);
        Assert.All(plan.Items, i => Assert.Equal(OrganiseItemStatus.Ready, i.Status));
    }

    [Fact]
    public void The_source_folders_are_listed_once_each()
    {
        var disk = new FakeDisk()
            .WithFile($@"{Src}\a.mp3")
            .WithFile($@"{Src}\b.mp3")
            .WithFile(@"C:\music\other\c.mp3")
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move,
                        [$@"{Src}\a.mp3", $@"{Src}\b.mp3", @"C:\music\other\c.mp3"]);

        Assert.Equal(2, plan.SourceFolders.Count);
    }

    [Fact]
    public void The_same_file_selected_twice_is_planned_once()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3", 100).WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3", $@"{SrcShouting}\A.MP3"]);

        Assert.Single(plan.Items);
        Assert.Equal(100, plan.TotalBytes);
    }

    // -- Collisions: there is no overwrite -------------------------------------------------------

    [Fact]
    public void A_taken_name_collides_and_is_skipped_by_default()
    {
        var disk = new FakeDisk()
            .WithFile($@"{Src}\a.mp3")
            .WithFile($@"{Dst}\a.mp3")
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [$@"{Src}\a.mp3"]);

        Assert.Equal(OrganiseItemStatus.Collides, plan.Items[0].Status);
        Assert.False(plan.Items[0].WillRun);
        Assert.Equal(1, plan.CollisionCount);
        Assert.False(plan.CanRun);   // nothing left that could run
    }

    [Fact]
    public void Keep_both_lands_it_under_the_first_free_suffixed_name()
    {
        var disk = new FakeDisk()
            .WithFile($@"{Src}\a.mp3")
            .WithFile($@"{Dst}\a.mp3")
            .WithFile($@"{Dst}\a (2).mp3")
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [$@"{Src}\a.mp3"],
                        policy: CollisionPolicy.KeepBoth);

        Assert.Equal(OrganiseItemStatus.KeepBoth, plan.Items[0].Status);
        Assert.Equal($@"{Dst}\a (3).mp3", plan.Items[0].DestinationPath);
        Assert.True(plan.Items[0].WillRun);
    }

    [Fact]
    public void Two_selected_files_with_the_same_name_do_not_land_on_each_other()
    {
        // The second one's destination is free ON DISK - it is the FIRST one's arrival that takes
        // it. Without the claim set, one of the two would silently disappear.
        var disk = new FakeDisk()
            .WithFile(@"C:\music\one\a.mp3")
            .WithFile(@"C:\music\two\a.mp3")
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [@"C:\music\one\a.mp3", @"C:\music\two\a.mp3"]);

        Assert.Equal(OrganiseItemStatus.Ready, plan.Items[0].Status);
        Assert.Equal(OrganiseItemStatus.Collides, plan.Items[1].Status);
        Assert.Equal(1, plan.RunCount);
    }

    [Fact]
    public void Two_selected_files_with_the_same_name_can_both_be_kept()
    {
        var disk = new FakeDisk()
            .WithFile(@"C:\music\one\a.mp3")
            .WithFile(@"C:\music\two\a.mp3")
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [@"C:\music\one\a.mp3", @"C:\music\two\a.mp3"],
                        policy: CollisionPolicy.KeepBoth);

        Assert.Equal($@"{Dst}\a.mp3", plan.Items[0].DestinationPath);
        Assert.Equal($@"{Dst}\a (2).mp3", plan.Items[1].DestinationPath);
        Assert.Equal(2, plan.RunCount);
    }

    [Fact]
    public void A_folder_in_the_way_counts_as_a_taken_name()
    {
        var disk = new FakeDisk()
            .WithFile($@"{Src}\a.mp3")
            .WithFolder(Dst)
            .WithFolder($@"{Dst}\a.mp3");

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3"]);

        Assert.Equal(OrganiseItemStatus.Collides, plan.Items[0].Status);
    }

    // -- The things that make an item impossible -------------------------------------------------

    [Fact]
    public void A_file_that_is_gone_is_reported_not_attempted()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3").WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [$@"{Src}\a.mp3", $@"{Src}\vanished.mp3"]);

        Assert.Equal(OrganiseItemStatus.SourceMissing, plan.Items[1].Status);
        Assert.Equal(1, plan.MissingCount);
        Assert.Equal(1, plan.RunCount);
    }

    [Fact]
    public void A_file_already_in_the_destination_is_left_alone()
    {
        var disk = new FakeDisk().WithFile($@"{Dst}\a.mp3").WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [$@"{Dst}\a.mp3"]);

        Assert.Equal(OrganiseItemStatus.SameFolder, plan.Items[0].Status);
        Assert.False(plan.CanRun);
    }

    [Fact]
    public void A_folder_handed_in_as_a_source_is_not_a_file()
    {
        var disk = new FakeDisk().WithFolder($@"{Src}\album").WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\album"]);

        Assert.Equal(OrganiseItemStatus.NotAFile, plan.Items[0].Status);
    }

    // -- The things that make the whole plan impossible ------------------------------------------

    [Fact]
    public void No_destination_blocks_everything()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3");

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3"], destination: null);

        Assert.Contains(OrganiseBlocker.NoDestination, plan.Blockers);
        Assert.False(plan.CanRun);
    }

    [Fact]
    public void A_destination_that_is_not_there_blocks_everything()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3");

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3"]);

        Assert.Contains(OrganiseBlocker.DestinationMissing, plan.Blockers);
        Assert.False(plan.CanRun);
    }

    [Fact]
    public void A_destination_that_refuses_writes_blocks_everything()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3").WithFolder(Dst);
        disk.ReadOnly.Add(Dst);

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3"]);

        Assert.Contains(OrganiseBlocker.DestinationNotWritable, plan.Blockers);
        Assert.False(plan.CanRun);
    }

    [Fact]
    public void A_destination_inside_a_selected_folder_blocks_everything()
    {
        var disk = new FakeDisk()
            .WithFolder(@"C:\music\album")
            .WithFolder(@"C:\music\album\extras");

        var plan = Plan(disk, OrganiseAction.Move, [@"C:\music\album"],
                        destination: @"C:\music\album\extras");

        Assert.Contains(OrganiseBlocker.DestinationInsideSelection, plan.Blockers);
    }

    [Fact]
    public void A_sibling_folder_with_the_same_prefix_is_not_inside_it()
    {
        // "...\music2" must not read as being inside "...\music" - the separator boundary rule.
        var disk = new FakeDisk()
            .WithFolder(@"C:\music")
            .WithFolder(@"C:\music2");

        var plan = Plan(disk, OrganiseAction.Move, [@"C:\music"], destination: @"C:\music2");

        Assert.DoesNotContain(OrganiseBlocker.DestinationInsideSelection, plan.Blockers);
    }

    [Fact]
    public void Nothing_selected_blocks_everything()
    {
        var plan = Plan(new FakeDisk().WithFolder(Dst), OrganiseAction.Copy, []);

        Assert.Contains(OrganiseBlocker.NoFiles, plan.Blockers);
        Assert.False(plan.CanRun);
    }

    // -- Volumes ---------------------------------------------------------------------------------

    [Fact]
    public void A_move_within_one_volume_is_not_cross_volume()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3").WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [$@"{Src}\a.mp3"]);

        Assert.False(plan.Items[0].CrossVolume);
        Assert.False(plan.AnyCrossVolume);
    }

    [Fact]
    public void A_move_to_another_volume_is_flagged()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3").WithFolder(@"\\nas\music\GENRES");
        disk.Volumes[@"\\nas\music"] = @"\\NAS\MUSIC";

        var plan = Plan(disk, OrganiseAction.Move, [$@"{Src}\a.mp3"],
                        destination: @"\\nas\music\GENRES");

        Assert.True(plan.Items[0].CrossVolume);
        Assert.True(plan.AnyCrossVolume);
    }

    [Fact]
    public void An_unknown_volume_is_treated_as_a_crossing()
    {
        // "Cannot tell" must read as "not a rename": copy - verify - remove is correct on one
        // volume too, only slower. The other way round would skip the verification.
        var probe = new NullVolumeDisk(new FakeDisk().WithFile($@"{Src}\a.mp3").WithFolder(Dst));
        var plan = OrganisePlanner.Plan(new OrganiseRequest
        {
            Action      = OrganiseAction.Move,
            Sources     = [$@"{Src}\a.mp3"],
            Destination = Dst,
        }, probe);

        Assert.True(plan.Items[0].CrossVolume);
    }

    private sealed class NullVolumeDisk(FakeDisk inner) : IFileProbe
    {
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public long SizeOf(string path) => inner.SizeOf(path);
        public string? VolumeOf(string path) => null;
        public bool CanWriteTo(string folder) => inner.CanWriteTo(folder);
    }

    // -- Delete ----------------------------------------------------------------------------------

    [Fact]
    public void Delete_needs_no_destination()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3", 512);

        var plan = Plan(disk, OrganiseAction.Delete, [$@"{Src}\a.mp3"]);

        Assert.True(plan.CanRun);
        Assert.Null(plan.Destination);
        Assert.Equal(OrganiseItemStatus.Ready, plan.Items[0].Status);
        Assert.Equal("", plan.Items[0].DestinationPath);
    }

    [Fact]
    public void A_delete_with_a_bin_is_planned_as_a_trip_to_the_bin()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3");

        var plan = Plan(disk, OrganiseAction.Delete, [$@"{Src}\a.mp3"]);

        Assert.Equal(DeleteKind.Recycle, plan.Deletion);
    }

    [Fact]
    public void A_delete_without_a_bin_is_planned_as_a_permanent_one_and_still_runs()
    {
        // The rule this feature used to turn on was "no bin means REFUSE". It was wrong: a dead
        // button leaves someone with no way to do a thing the window offers. The delete happens, it
        // is planned as PERMANENT, and the preview says so before anything is pressed.
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3");

        var plan = Plan(disk, OrganiseAction.Delete, [$@"{Src}\a.mp3"], bin: false);

        Assert.Equal(DeleteKind.Permanent, plan.Deletion);
        Assert.Empty(plan.Blockers);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void A_copy_and_a_move_have_no_delete_kind_at_all()
    {
        // Null rather than a default: "this plan deletes nothing" is a different fact from "this
        // delete is the reversible one", and a caller reporting what happened needs to tell them
        // apart.
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3").WithFolder(Dst);

        Assert.Null(Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3"]).Deletion);
        Assert.Null(Plan(disk, OrganiseAction.Move, [$@"{Src}\a.mp3"]).Deletion);
    }

    [Fact]
    public void The_platform_that_has_no_bin_says_so_through_NoRecycleBin()
    {
        // NoRecycleBin survives as INFORMATION - "there is nowhere to file this here" - and no
        // longer as a refusal. This is the path a host on such a platform takes.
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3");

        var plan = OrganisePlanner.Plan(new OrganiseRequest
        {
            Action              = OrganiseAction.Delete,
            Sources             = [$@"{Src}\a.mp3"],
            RecycleBinAvailable = NoRecycleBin.Instance.IsAvailable,
        }, disk);

        Assert.Equal(DeleteKind.Permanent, plan.Deletion);
        Assert.True(plan.CanRun);
    }

    // -- What the preview says -------------------------------------------------------------------

    [Fact]
    public void The_summary_names_the_count_the_size_and_the_leftovers()
    {
        var disk = new FakeDisk()
            .WithFile($@"{Src}\a.mp3", 1024 * 1024)
            .WithFile($@"{Src}\b.mp3", 1024 * 1024)
            .WithFile($@"{Dst}\b.mp3")
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Move, [$@"{Src}\a.mp3", $@"{Src}\b.mp3"]);
        var summary = OrganiseText.Summarise(plan);

        Assert.Contains("1 file is moved", summary, StringComparison.Ordinal);
        Assert.Contains("1.0 MB", summary, StringComparison.Ordinal);
        Assert.Contains("1 is left alone", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_leads_with_the_blocker_when_there_is_one()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3");

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3"], destination: null);

        Assert.Equal(OrganiseText.Describe(OrganiseBlocker.NoDestination),
                     OrganiseText.Summarise(plan));
    }

    [Fact]
    public void A_kept_both_row_says_the_name_it_landed_under()
    {
        var disk = new FakeDisk()
            .WithFile($@"{Src}\a.mp3")
            .WithFile($@"{Dst}\a.mp3")
            .WithFolder(Dst);

        var plan = Plan(disk, OrganiseAction.Copy, [$@"{Src}\a.mp3"],
                        policy: CollisionPolicy.KeepBoth);

        Assert.Contains("a (2).mp3", OrganiseText.Describe(plan.Items[0]), StringComparison.Ordinal);
    }

    // -- The words of the two deletes ------------------------------------------------------------
    //
    // The wording IS the feature here. A permanent delete is the one thing in this suite that
    // nothing can take back, so the headline, the caution box and the button all have to carry it -
    // and there is no second dialog behind them to catch a misread.

    [Fact]
    public void The_headline_says_which_kind_of_delete_this_is()
    {
        var disk = new FakeDisk().WithFile($@"{Src}\a.mp3").WithFile($@"{Src}\b.mp3");
        string[] sources = [$@"{Src}\a.mp3", $@"{Src}\b.mp3"];

        var recycled = OrganiseText.Summarise(Plan(disk, OrganiseAction.Delete, sources));
        var forGood  = OrganiseText.Summarise(Plan(disk, OrganiseAction.Delete, sources, bin: false));

        Assert.Equal("2 files are deleted", recycled);
        Assert.Equal("2 files are deleted for good", forGood);
    }

    [Fact]
    public void The_button_is_the_acknowledgement_when_the_delete_is_permanent()
    {
        // The preview is the confirmation in this feature, so the words being PRESSED have to be the
        // words that say what happens.
        Assert.Equal("Delete 12 files",
                     OrganiseText.RunLabel(OrganiseAction.Delete, 12, DeleteKind.Recycle));
        Assert.Equal("Delete 12 files for good",
                     OrganiseText.RunLabel(OrganiseAction.Delete, 12, DeleteKind.Permanent));
        Assert.Equal("Delete 1 file for good",
                     OrganiseText.RunLabel(OrganiseAction.Delete, 1, DeleteKind.Permanent));

        // Nothing else grows a suffix.
        Assert.Equal("Move 3 files", OrganiseText.RunLabel(OrganiseAction.Move, 3));
        Assert.Equal("Copy 1 file", OrganiseText.RunLabel(OrganiseAction.Copy, 1));
    }

    [Fact]
    public void The_recycling_delete_names_the_bin_and_gets_no_headline()
    {
        var caution = OrganiseText.DeleteCaution(DeleteKind.Recycle, "Recycle Bin", 4);

        Assert.Contains("Recycle Bin", caution, StringComparison.Ordinal);
        Assert.Contains("get them back", caution, StringComparison.Ordinal);

        // No lead line: an ordinary, reversible thing with a headline on it teaches people to read
        // past the headline that matters.
        Assert.Equal("", OrganiseText.DeleteCautionLead(DeleteKind.Recycle));
    }

    [Fact]
    public void The_permanent_delete_states_it_names_the_count_and_blames_nobody()
    {
        var lead    = OrganiseText.DeleteCautionLead(DeleteKind.Permanent);
        var caution = OrganiseText.DeleteCaution(DeleteKind.Permanent, "recycle bin", 12);

        Assert.Contains("permanent", lead, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no recycle bin", lead, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("12 files", caution, StringComparison.Ordinal);
        Assert.Contains("nothing here or in the system can bring them back",
                        caution, StringComparison.Ordinal);

        // It must not read as a scare or as an accusation. Deleting a track you no longer want is a
        // legitimate thing to ask for.
        foreach (var word in new[] { "WARNING", "DANGER", "careful", "sure?", "really" })
            Assert.DoesNotContain(word, caution, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_permanent_wording_stays_grammatical_for_one_file_and_for_none()
    {
        var one = OrganiseText.DeleteCaution(DeleteKind.Permanent, "recycle bin", 1);
        Assert.Contains("That file is removed", one, StringComparison.Ordinal);
        Assert.Contains("bring it back", one, StringComparison.Ordinal);

        // Nothing is running, so there is nothing to count - and "0 files" is not a sentence.
        var none = OrganiseText.DeleteCaution(DeleteKind.Permanent, "recycle bin", 0);
        Assert.DoesNotContain("0 file", none, StringComparison.Ordinal);
        Assert.Contains("removed from the disk", none, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_blocker_and_every_status_has_words()
    {
        // A member added without copy would otherwise surface as an empty label on screen.
        foreach (var blocker in Enum.GetValues<OrganiseBlocker>())
            Assert.NotEqual("This cannot run.", OrganiseText.Describe(blocker));

        foreach (var status in Enum.GetValues<OrganiseItemStatus>())
        {
            if (status == OrganiseItemStatus.Ready) continue;   // "nothing is in the way" says nothing
            var item = new OrganiseItem
            {
                SourcePath = @"C:\x\a.mp3",
                DestinationPath = @"C:\y\a (2).mp3",
                Status = status,
            };
            Assert.NotEqual("", OrganiseText.Describe(item));
        }
    }
}
