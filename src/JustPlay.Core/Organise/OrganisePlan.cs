using System;
using System.Collections.Generic;
using System.Linq;

namespace JustPlay.Core.Organise;

/// <summary>What an organise operation does to the files it was handed.</summary>
public enum OrganiseAction
{
    /// <summary>Put a duplicate in the destination. The source stays exactly where it is - this is
    /// the safe default in every ambiguous moment, because nothing it does can lose a track.</summary>
    Copy,

    /// <summary>Put the file in the destination and take it out of the folder it came from.</summary>
    Move,

    /// <summary>Take the file away. Which KIND of taking away is <see cref="DeleteKind"/>, and it is
    /// decided by whether this platform has a bin at all.</summary>
    Delete,
}

/// <summary>
/// The two ways a delete can end, and the reason the preview has to name one of them.
///
/// <para>Where the operating system has a bin, a delete is a filing job: the track is out of the way
/// and a wrong click on a Friday night is a trip to the bin, not a re-purchase. Where it has none,
/// there is no third option to fall back on - the choice is between removing the file for good and
/// not offering a delete at all, and refusing leaves someone with no way to do a thing the app
/// otherwise offers. So it happens, and the window states plainly which of the two it is BEFORE the
/// button is pressed. This is the one operation in the suite that nothing can take back, and it is
/// the only one that says so in the button's own words.</para>
/// </summary>
public enum DeleteKind
{
    /// <summary>To the operating system's bin, where it can be fetched back. The default, and what
    /// happens on every platform that has one.</summary>
    Recycle,

    /// <summary>Off the disk. Chosen only because this platform has no bin, never as a preference,
    /// and never without the preview having said so.</summary>
    Permanent,
}

/// <summary>
/// What happens when the destination already holds a file of that name.
///
/// <para>(!!) There are two members and there will never be a third. Overwriting a music file is
/// unrecoverable: the bytes are gone, and a DJ finds out about it on a Friday night. A destination
/// that already holds the name is a COLLISION, it is named in the preview, and the default answer is
/// to leave both files alone. "Keep both" is offered because writing a second copy under a different
/// name destroys nothing. "Replace" is not offered at all.</para>
/// </summary>
public enum CollisionPolicy
{
    /// <summary>Leave the file where it is and leave the destination's copy alone. The default.</summary>
    Skip,

    /// <summary>Write it under a free name next to the one already there - "track (2).mp3".</summary>
    KeepBoth,
}

/// <summary>A reason the whole plan cannot run. Nothing happens while a plan has one.</summary>
public enum OrganiseBlocker
{
    /// <summary>Nothing was selected.</summary>
    NoFiles,

    /// <summary>A move or a copy without somewhere to go.</summary>
    NoDestination,

    /// <summary>The destination folder is not there - an unmounted share, a typed path, a folder
    /// that was renamed while the window was open.</summary>
    DestinationMissing,

    /// <summary>The destination exists but refuses to be written to.</summary>
    DestinationNotWritable,

    /// <summary>The destination is inside something that was selected - the operation would be
    /// putting a folder into itself.</summary>
    DestinationInsideSelection,
}

/// <summary>What will happen to one file. Every item in a plan carries one of these, and the
/// preview shows it - "what will happen to each" is the whole point of previewing.</summary>
public enum OrganiseItemStatus
{
    /// <summary>Nothing is in the way.</summary>
    Ready,

    /// <summary>The name is taken and "keep both" is on, so it lands under a free name instead.</summary>
    KeepBoth,

    /// <summary>The name is taken and the answer is to leave both files alone. Skipped.</summary>
    Collides,

    /// <summary>The file is not on disk any more. Skipped.</summary>
    SourceMissing,

    /// <summary>It is already in the destination folder. Skipped - a move to where it already is
    /// is nothing, and a copy would be a copy of a file onto itself.</summary>
    SameFolder,

    /// <summary>The path is a folder, not a track. Skipped: this operates on files.</summary>
    NotAFile,
}

/// <summary>One file's line in the plan: where it is, where it would land, and what stops it.</summary>
public sealed record OrganiseItem
{
    /// <summary>The file as it is now.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Where it would land. Empty for a delete - there is no destination, and pretending
    /// there is one ("...\$Recycle.Bin\...") would be inventing a path nobody can use.</summary>
    public string DestinationPath { get; init; } = "";

    public required OrganiseItemStatus Status { get; init; }

    /// <summary>Size on disk, or 0 when it could not be read. Only the running items are summed.</summary>
    public long Bytes { get; init; }

    /// <summary>
    /// True when source and destination are on different volumes, so a move cannot be a rename and
    /// has to be copy - verify - remove. Surfaced because it changes what the operation IS, and
    /// because it is the difference between instant and minutes over SMB.
    /// </summary>
    public bool CrossVolume { get; init; }

    /// <summary>True when this item is actually going to be touched.</summary>
    public bool WillRun => Status is OrganiseItemStatus.Ready or OrganiseItemStatus.KeepBoth;
}

/// <summary>What the caller asked for. Everything the planner needs and nothing it does not.</summary>
public sealed record OrganiseRequest
{
    public required OrganiseAction Action { get; init; }

    /// <summary>The selected files, in the order they were selected.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>The destination folder. Ignored - and expected to be null - for a delete.</summary>
    public string? Destination { get; init; }

    public CollisionPolicy Collisions { get; init; } = CollisionPolicy.Skip;

    /// <summary>
    /// Whether this machine has a recycle bin we can actually reach. Passed IN rather than probed, so
    /// the planner stays pure and "which kind of delete is this" is a plain table-driven test.
    ///
    /// <para>It decides <see cref="OrganisePlan.Deletion"/> and nothing else. False does not block a
    /// delete; it makes the delete a permanent one and makes the preview say so.</para>
    /// </summary>
    public bool RecycleBinAvailable { get; init; } = true;
}

/// <summary>
/// The whole answer, computed BEFORE anything happens: the per-file plan, the collisions, whether a
/// move is a rename or a copy across volumes, the total bytes, and what makes the operation
/// impossible.
///
/// <para>Nothing here has touched a file except to ask about it, and it is the same value the runner
/// then executes - so the preview cannot drift into promising something else (the same rule
/// <c>TagSaveConfirmWindow</c> follows for tag writes).</para>
/// </summary>
public sealed record OrganisePlan
{
    public required OrganiseAction Action { get; init; }

    public string? Destination { get; init; }

    public CollisionPolicy Collisions { get; init; }

    /// <summary>
    /// For a DELETE: whether these files go to the bin or off the disk. Null for a copy or a move,
    /// which take nothing away.
    ///
    /// <para>It rides on the PLAN rather than being re-decided at run time, for the same reason
    /// everything else here does: the preview and the run have to be the same value, so what the
    /// window said cannot drift from what happens. The runner refuses a plan that promised the bin
    /// on a machine that turns out not to have one - it never quietly upgrades to permanent.</para>
    /// </summary>
    public DeleteKind? Deletion { get; init; }

    /// <summary>One line per selected file, in the order it was selected.</summary>
    public required IReadOnlyList<OrganiseItem> Items { get; init; }

    /// <summary>Reasons the whole thing cannot run. Empty means it can.</summary>
    public IReadOnlyList<OrganiseBlocker> Blockers { get; init; } = [];

    /// <summary>The distinct folders the selection came out of - the "from where" of the preview.</summary>
    public IReadOnlyList<string> SourceFolders { get; init; } = [];

    public int RunCount => Items.Count(i => i.WillRun);

    public int SkipCount => Items.Count - RunCount;

    public int CollisionCount =>
        Items.Count(i => i.Status is OrganiseItemStatus.Collides or OrganiseItemStatus.KeepBoth);

    public int MissingCount => Items.Count(i => i.Status == OrganiseItemStatus.SourceMissing);

    /// <summary>Bytes of the items that will actually run.</summary>
    public long TotalBytes => Items.Where(i => i.WillRun).Sum(i => i.Bytes);

    /// <summary>True when at least one running item has to cross a volume.</summary>
    public bool AnyCrossVolume => Items.Any(i => i.WillRun && i.CrossVolume);

    /// <summary>Whether pressing the button would do anything at all.</summary>
    public bool CanRun => Blockers.Count == 0 && RunCount > 0;

    /// <summary>An empty plan for a window that has not been given a destination yet.</summary>
    public static OrganisePlan Empty(OrganiseAction action) => new()
    {
        Action   = action,
        Items    = [],
        Blockers = [OrganiseBlocker.NoFiles],
    };
}

/// <summary>
/// The words the preview uses, kept next to the model rather than in the window.
///
/// <para>They are part of what is being tested: "12 files are moved, 2 are left alone" is the
/// sentence the whole feature is judged on, and a sentence that lives in XAML is a sentence nobody
/// can assert on.</para>
/// </summary>
public static class OrganiseText
{
    /// <summary>The action as a verb, for a button and a title.</summary>
    public static string Verb(OrganiseAction action) => action switch
    {
        OrganiseAction.Copy   => "Copy",
        OrganiseAction.Move   => "Move",
        OrganiseAction.Delete => "Delete",
        _                     => "Organise",
    };

    /// <summary>
    /// The action in the past tense, for a result line. A permanent delete says which one it was:
    /// "12 deleted" and "12 deleted for good" are not the same news.
    /// </summary>
    public static string Past(OrganiseAction action, DeleteKind? deletion = null) => action switch
    {
        OrganiseAction.Copy   => "copied",
        OrganiseAction.Move   => "moved",
        OrganiseAction.Delete => deletion == DeleteKind.Permanent ? "deleted for good" : "deleted",
        _                     => "done",
    };

    /// <summary>Why nothing can run. One plain sentence, no jargon, no error code.</summary>
    public static string Describe(OrganiseBlocker blocker) => blocker switch
    {
        OrganiseBlocker.NoFiles                    => "Nothing is selected.",
        OrganiseBlocker.NoDestination              => "Choose a folder to put these in.",
        OrganiseBlocker.DestinationMissing         => "That folder is not there - the drive or share may be offline.",
        OrganiseBlocker.DestinationNotWritable     => "That folder cannot be written to.",
        OrganiseBlocker.DestinationInsideSelection => "That folder is inside what you selected.",
        _                                          => "This cannot run.",
    };

    /// <summary>
    /// The button's own words: the verb, the count, and - for the delete that cannot be taken back -
    /// the fact that it cannot.
    ///
    /// <para>The button is the confirming action, and there is no second dialog behind it: this
    /// window's preview IS the confirmation. So the words the user presses have to be the words that
    /// acknowledge what happens, not a neutral "Delete" over a warning they may have read past.</para>
    /// </summary>
    public static string RunLabel(OrganiseAction action, int count, DeleteKind? deletion = null)
    {
        var label = $"{Verb(action)} {Files(count)}";
        return deletion == DeleteKind.Permanent ? label + " for good" : label;
    }

    /// <summary>
    /// The short bold line at the top of a delete's caution box, or empty where none is needed.
    ///
    /// <para>A recycling delete gets no lead - it is an ordinary, reversible thing and a headline on
    /// it would train people to read past the one that matters. The permanent case gets one, because
    /// it is genuinely different and a paragraph that LOOKS like every other paragraph is not a
    /// difference anyone sees.</para>
    /// </summary>
    public static string DeleteCautionLead(DeleteKind deletion) =>
        deletion == DeleteKind.Permanent
            ? "No recycle bin on this system - this delete is permanent."
            : "";

    /// <summary>
    /// What a delete actually does, in the box above the list.
    ///
    /// <para>Warm caution, not alarm. Deleting a track you no longer want is a legitimate thing to
    /// do, and the copy states the consequence without implying the choice was a mistake.</para>
    /// </summary>
    /// <param name="deletion">Which kind of delete this plan holds.</param>
    /// <param name="binName">What this platform calls its bin - "Recycle Bin", "Trash".</param>
    /// <param name="count">How many files will actually be deleted. 0 drops the count from the
    /// sentence rather than saying "0 files": nothing is running, so there is nothing to count.</param>
    public static string DeleteCaution(DeleteKind deletion, string binName, int count)
    {
        if (deletion != DeleteKind.Permanent)
            return $"These go to the {(string.IsNullOrWhiteSpace(binName) ? "recycle bin" : binName)}, " +
                   "where you can get them back. A file on a network share often has no bin - one " +
                   "that cannot be recycled is reported and left where it is.";

        var subject = count switch
        {
            <= 0 => "A file deleted here is removed",
            1    => "That file is removed",
            _    => $"Those {ByteSize.Count(count)} files are removed",
        };

        return $"{subject} from the disk itself, and nothing here or in the system can bring " +
               (count <= 1 ? "it" : "them") +
               " back afterwards - so the list below is worth one more look.";
    }

    /// <summary>What happens to one file, in the words the preview row shows.</summary>
    public static string Describe(OrganiseItem item) => item.Status switch
    {
        OrganiseItemStatus.Ready         => "",
        OrganiseItemStatus.KeepBoth      => "already there - kept as " + FileNamePart(item.DestinationPath),
        OrganiseItemStatus.Collides      => "already there - left alone",
        OrganiseItemStatus.SourceMissing => "not on disk any more",
        OrganiseItemStatus.SameFolder    => "already in this folder",
        OrganiseItemStatus.NotAFile      => "not a file",
        _                                => "",
    };

    /// <summary>
    /// The headline: how many files, and how many of them are being left alone. This is the one
    /// sentence that decides whether she presses the button or goes back.
    /// </summary>
    public static string Summarise(OrganisePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Blockers.Count > 0) return Describe(plan.Blockers[0]);
        if (plan.Items.Count == 0) return Describe(OrganiseBlocker.NoFiles);

        var run   = plan.RunCount;
        var parts = new List<string>(3);

        parts.Add(run == 1
            ? $"1 file is {Past(plan.Action, plan.Deletion)}"
            : $"{ByteSize.Count(run)} files are {Past(plan.Action, plan.Deletion)}");

        if (plan.Action != OrganiseAction.Delete && plan.TotalBytes > 0)
            parts.Add(ByteSize.Format(plan.TotalBytes));

        var skipped = plan.SkipCount;
        if (skipped > 0)
            parts.Add(skipped == 1 ? "1 is left alone" : $"{ByteSize.Count(skipped)} are left alone");

        return string.Join("  -  ", parts);
    }

    /// <summary>"1 file" or "12 files" - the count phrase every headline and button uses, so they
    /// cannot disagree about the plural.</summary>
    private static string Files(int count) =>
        count == 1 ? "1 file" : $"{ByteSize.Count(count)} files";

    private static string FileNamePart(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
