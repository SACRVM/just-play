using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Organise;
using JustPlay.UI.Controls;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// One line of the organise preview: the file, where it lands, and what is in its way.
///
/// <para>A row per FILE, and every selected file gets one - including the ones that will be left
/// alone. A preview that quietly dropped the skipped rows would answer "what happens to these 12"
/// with a list of 10.</para>
/// </summary>
public sealed record OrganiseRow(
    string Name, string Folder, string SourcePath, string Issue, bool IsProblem, bool WillRun,
    bool ShowFolder);

/// <summary>A folder that was used as a destination before - its full path, and the short name the
/// chip shows. Named FullPath rather than Path so the binding reads as a property and not as WPF's
/// old <c>{Binding Path=...}</c> syntax.</summary>
public sealed record RecentFolder(string FullPath, string Name);

/// <summary>
/// MOVE / COPY / DELETE for a selection of tracks - the preview, the choices, and the run.
///
/// <para><b>Nothing happens before the preview.</b> The list below the destination is not a
/// courtesy: this repo has no undo by design, so the protection goes in FRONT of the action. It
/// states how many files, from where, to where, which ones collide and what happens to each - and
/// the button is dead until there is something it can honestly do.</para>
///
/// <para><b>There is no overwrite, and there is no option for one.</b> A name the destination
/// already holds is a collision; the default answer leaves both files alone, and the only other
/// answer writes the arrival under a free name. Overwriting a track cannot be taken back.</para>
///
/// <para><b>A delete goes to the operating system's bin where there is one.</b> Where there is none,
/// it is a permanent delete - and the window says which of the two it is in the caution box, in the
/// headline, and in the words on the button you press. A dead button would leave someone with no way
/// to do a thing this window offers; a silent permanent delete would be worse than either. So it is
/// stated, and the choice is theirs.</para>
///
/// <para>Hand-rolled <see cref="INotifyPropertyChanged"/>, like the rest of the shared UI library.
/// The window holds no rules: every decision is <see cref="OrganisePlanner"/>'s and every byte is
/// <see cref="OrganiseRunner"/>'s, so the same operation could be driven from a second host - or
/// from a test - without a window at all.</para>
/// </summary>
public sealed class OrganiseViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<string> _sources;
    private readonly IRecycleBin _bin;
    private readonly IFileProbe _probe;
    private readonly IOrganiseIndexSync? _index;
    private readonly Action<IReadOnlyList<string>>? _release;

    private OrganisePlan _plan;
    private CancellationTokenSource? _run;

    /// <param name="action">Move, copy or delete.</param>
    /// <param name="sources">The selected files.</param>
    /// <param name="bin">This machine's bin. <c>NoRecycleBin</c> states that there is none, which
    /// makes a delete here a permanent one - previewed and labelled as such, not refused. Required:
    /// a window that deletes tracks may not guess where they go.</param>
    /// <param name="index">Optional - the library index to reconcile afterwards, so a moved track is
    /// never lost from the library. Null in an app that has no index.</param>
    /// <param name="release">Optional - called on the UI thread with the paths about to be touched,
    /// so the host can let go of a file it is previewing. A file with an open handle cannot be moved
    /// or deleted on Windows.</param>
    /// <param name="probe">The disk. Swappable so the whole window is testable without one.</param>
    public OrganiseViewModel(
        OrganiseAction action,
        IReadOnlyList<string> sources,
        IRecycleBin bin,
        IOrganiseIndexSync? index = null,
        Action<IReadOnlyList<string>>? release = null,
        IFileProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(bin);

        Action = action;
        _sources = sources;
        _bin = bin;
        _index = index;
        _release = release;
        _probe = probe ?? SystemFileProbe.Instance;

        Recents = action == OrganiseAction.Delete ? [] : LoadRecents();
        _plan = Replan();
    }

    public OrganiseAction Action { get; }

    /// <summary>The chrome mark - a folder for the two that put files somewhere, a bin for the one
    /// that takes them away. A shipped vector, never a character.</summary>
    public IconKind Icon =>
        Action == OrganiseAction.Delete ? IconKind.Trash : IconKind.Folder;

    /// <summary>The chrome title - short, spaced, the suite's house style.</summary>
    public string Title => Action switch
    {
        OrganiseAction.Move   => "MOVE TO",
        OrganiseAction.Copy   => "COPY TO",
        OrganiseAction.Delete => "DELETE",
        _                     => "ORGANISE",
    };

    /// <summary>False for a delete, which has nowhere to go.</summary>
    public bool ShowsDestination => Action != OrganiseAction.Delete;

    // -- The destination -------------------------------------------------------------------------

    private string? _destination;

    /// <summary>Where the files go. Setting it re-plans, which is cheap - no file is opened.</summary>
    public string? Destination
    {
        get => _destination;
        set
        {
            if (string.Equals(_destination, value, StringComparison.OrdinalIgnoreCase)) return;
            _destination = value;
            Raise();
            Raise(nameof(DestinationName));
            Raise(nameof(DestinationText));
            Raise(nameof(HasDestination));
            Refresh();
        }
    }

    public bool HasDestination => !string.IsNullOrWhiteSpace(_destination);

    /// <summary>
    /// The folder's own NAME, on its own line and bright.
    ///
    /// <para>A library path is long and its meaningful end is the leaf - "...\GENRES\Hard Techno".
    /// One trimmed line shows the reach up to the share and loses exactly the word that says where
    /// the files are going, so the leaf gets its own line and the full path sits under it.</para>
    /// </summary>
    public string DestinationName =>
        _destination is { Length: > 0 } d ? ShortName(d) : "Choose a folder";

    public string DestinationText => _destination ?? "";

    /// <summary>The folders things were last put in, so the common case is one click.</summary>
    public IReadOnlyList<RecentFolder> Recents { get; private set; }

    public bool HasRecents => Recents.Count > 0;

    // -- The collision answer --------------------------------------------------------------------

    private bool _keepBoth;

    /// <summary>
    /// Off means a taken name leaves both files alone. On means the arrival lands under a free name.
    /// (!!) There is deliberately no third choice - see the class doc.
    /// </summary>
    public bool KeepBoth
    {
        get => _keepBoth;
        set
        {
            if (_keepBoth == value) return;
            _keepBoth = value;
            Raise();
            Refresh();
        }
    }

    /// <summary>Only worth showing the choice when something actually collides.</summary>
    public bool HasCollisions => _plan.CollisionCount > 0;

    public string CollisionText => _plan.CollisionCount == 1
        ? "1 name is already taken there"
        : $"{ByteSize.Count(_plan.CollisionCount)} names are already taken there";

    // -- The plan --------------------------------------------------------------------------------

    /// <summary>One row per selected file, skipped ones included.</summary>
    public IReadOnlyList<OrganiseRow> Rows { get; private set; } = [];

    /// <summary>The headline sentence - the one line that decides whether she presses the button.</summary>
    public string Summary => OrganiseText.Summarise(_plan);

    /// <summary>
    /// Where the folder picker should open. The destination already chosen, else the last one used,
    /// else the folder the selection came out of - never wherever the OS feels like, which on two
    /// monitors and a NAS is the classic way to lose thirty seconds.
    /// </summary>
    public string? PickerStart =>
        _destination
        ?? Recents.FirstOrDefault()?.FullPath
        ?? _plan.SourceFolders.FirstOrDefault();

    /// <summary>The "from where", short enough for a 36 px chrome bar - the leaf again, for the same
    /// reason the destination shows one. The whole path is the tooltip.</summary>
    public string FromText => _plan.SourceFolders.Count switch
    {
        0 => "",
        1 => ShortName(_plan.SourceFolders[0]),
        var n => $"{ByteSize.Count(n)} folders",
    };

    /// <summary>The full path behind <see cref="FromText"/>, for the tooltip.</summary>
    public string FromTip => _plan.SourceFolders.Count == 1 ? _plan.SourceFolders[0] : FromText;

    /// <summary>
    /// A consequence worth naming - the warm bordered box with the caution mark, the suite's tone
    /// for "this is a legitimate choice that does something you should know about". Reserved for the
    /// two cases that qualify: a delete, and a move that has to copy and verify across volumes.
    ///
    /// <para>It is deliberately NOT used for "the originals stay where they are". A caution mark on
    /// a non-event trains you to ignore the caution mark. It is warm sand and never red: red is for
    /// errors, and none of this is an error - it is a legitimate thing to ask for.</para>
    ///
    /// <para>The delete wording lives in <see cref="OrganiseText"/> beside the model, because it is
    /// the sentence the whole feature is judged on and a sentence in a view is one no test can
    /// assert on.</para>
    /// </summary>
    public string Caution => Action switch
    {
        OrganiseAction.Delete =>
            OrganiseText.DeleteCaution(Deletion, _bin.Name, _plan.RunCount),

        OrganiseAction.Move when HasDestination && _plan.AnyCrossVolume =>
            "That is a different drive, so each file is copied, checked byte for byte against the " +
            "original, and only then removed from where it is now. A file that does not check out " +
            "is left exactly where it is.",

        _ => "",
    };

    public bool HasCaution => Caution.Length > 0;

    /// <summary>
    /// The one short bold line above the caution paragraph, for the case that has to read
    /// DIFFERENTLY and not merely longer: a delete on a machine with no bin. Empty everywhere else.
    /// </summary>
    public string CautionLead => Action == OrganiseAction.Delete
        ? OrganiseText.DeleteCautionLead(Deletion)
        : "";

    public bool HasCautionLead => CautionLead.Length > 0;

    /// <summary>Which kind of delete this is. <see cref="DeleteKind.Recycle"/> for everything that
    /// is not a delete, so the copy helpers have one plain answer to switch on.</summary>
    private DeleteKind Deletion => _plan.Deletion ?? DeleteKind.Recycle;

    /// <summary>
    /// Plain information, one dim line, no mark: what this operation is when there is nothing to be
    /// careful about. A copy says nothing at all - the title and the summary already do.
    /// </summary>
    public string Notice =>
        Action == OrganiseAction.Move && HasDestination && !_plan.AnyCrossVolume
            ? "Same drive - each file is renamed into place, which is instant."
            : "";

    public bool HasNotice => Notice.Length > 0;

    /// <summary>True when there is nothing in the way of the whole operation.</summary>
    public bool CanRun => !IsBusy && _plan.CanRun;

    /// <summary>
    /// The button's own words, carrying the count - "Move 12 files", and for the delete that cannot
    /// be taken back, "Delete 12 files for good".
    ///
    /// <para>There is no second dialog behind this button: the preview IS the confirmation here. So
    /// the words being pressed are the words that acknowledge what happens.</para>
    /// </summary>
    public string RunLabel => OrganiseText.RunLabel(Action, _plan.RunCount, _plan.Deletion);

    /// <summary>What the run did, once it has. Null while it has not.</summary>
    public string? Result { get; private set; }

    public bool HasResult => Result is { Length: > 0 };

    /// <summary>True when a failure means the window should stay open rather than close.</summary>
    public bool Failed { get; private set; }

    // -- Busy ------------------------------------------------------------------------------------

    private bool _isBusy;
    private string? _busyText;
    private string? _busyDetail;
    private double? _busyProgress;

    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); Raise(nameof(CanRun)); Raise(nameof(IsIdle)); }
    }

    /// <summary>The inverse, so the controls can be disabled while a run is in flight.</summary>
    public bool IsIdle => !_isBusy;

    public string? BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    public string? BusyDetail { get => _busyDetail; private set => Set(ref _busyDetail, value); }

    public double? BusyProgress { get => _busyProgress; private set => Set(ref _busyProgress, value); }

    // -- Doing it --------------------------------------------------------------------------------

    /// <summary>Re-read the disk and rebuild the plan. Cheap - a handful of stats, no file opened.</summary>
    public void Refresh()
    {
        _plan = Replan();

        Raise(nameof(Rows));
        Raise(nameof(Summary));
        Raise(nameof(FromText));
        Raise(nameof(FromTip));
        Raise(nameof(Caution));
        Raise(nameof(HasCaution));
        Raise(nameof(CautionLead));
        Raise(nameof(HasCautionLead));
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
        Raise(nameof(HasCollisions));
        Raise(nameof(CollisionText));
        Raise(nameof(CanRun));
        Raise(nameof(RunLabel));
    }

    /// <summary>
    /// Carry the plan out. Returns what happened - including the paths an index still has to be told
    /// about, which is why a cancelled run reports rather than throws.
    /// </summary>
    public async Task<OrganiseReport> RunAsync()
    {
        var plan = _plan;
        if (!plan.CanRun)
            return new OrganiseReport { Action = Action, Skipped = plan.Items.Count };

        // A file JUST TAG is previewing has an open handle, and Windows will not move or delete it.
        // Released on the UI thread, before a single byte is touched.
        _release?.Invoke([.. plan.Items.Where(i => i.WillRun).Select(i => i.SourcePath)]);

        var run = new CancellationTokenSource();
        _run = run;
        IsBusy = true;
        BusyText = $"{OrganiseText.Verb(Action)}...";
        BusyDetail = null;
        BusyProgress = plan.TotalBytes > 0 ? 0 : null;
        Result = null;
        Failed = false;

        var progress = new Progress<OrganiseProgress>(OnProgress);

        try
        {
            var runner = new OrganiseRunner(_bin);
            var report = await Task.Run(
                () => runner.RunAsync(plan, progress, run.Token), CancellationToken.None)
                .ConfigureAwait(true);

            // Never leave a song behind: whatever moved has to reach the library index, or a track
            // that is simply somewhere else reads as gone. Off the UI thread - it opens a database
            // and reads tags.
            if (_index is { } index && (report.AddedPaths.Count > 0 || report.RemovedPaths.Count > 0))
            {
                BusyText = "Updating the library...";
                BusyDetail = null;
                BusyProgress = null;
                await Task.Run(() => index.PathsChanged(report.AddedPaths, report.RemovedPaths))
                          .ConfigureAwait(true);
            }

            if (report.Succeeded > 0 && Destination is { Length: > 0 } kept)
            {
                RecentDestinations.Remember(kept);
                Recents = LoadRecents();
                Raise(nameof(Recents));
                Raise(nameof(HasRecents));
            }

            Failed = report.Failed > 0;
            Result = ResultText(report);
            Raise(nameof(Result));
            Raise(nameof(HasResult));

            // (!!) Re-plan against the disk as it is NOW. The window stays open when anything failed,
            // and a preview built before the run still lists the files that did move - press the
            // button again and it would go looking for tracks that are already at their destination.
            // Re-planning turns that list into what is actually left: the ones that failed, and a
            // dead button when there is nothing left to do.
            Refresh();
            return report;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            BusyDetail = null;
            BusyProgress = null;
            _run = null;
            run.Dispose();
        }
    }

    /// <summary>Stop after the file in flight. Whatever already landed stays landed - a cancelled
    /// move is not rolled back, it is simply not continued.</summary>
    public void Cancel()
    {
        try { _run?.Cancel(); }
        catch (ObjectDisposedException) { /* the run finished between the click and here */ }
    }

    // -- Internals -------------------------------------------------------------------------------

    private OrganisePlan Replan()
    {
        var plan = OrganisePlanner.Plan(new OrganiseRequest
        {
            Action              = Action,
            Sources             = _sources,
            Destination         = Action == OrganiseAction.Delete ? null : _destination,
            Collisions          = _keepBoth ? CollisionPolicy.KeepBoth : CollisionPolicy.Skip,
            RecycleBinAvailable = _bin.IsAvailable,
        }, _probe);

        // The folder only earns a line per row when the selection actually spans several - one
        // folder is already named once, at the top.
        var showFolders = plan.SourceFolders.Count > 1;
        Rows = [.. plan.Items.Select(i => ToRow(i, showFolders))];
        return plan;
    }

    private static OrganiseRow ToRow(OrganiseItem item, bool showFolder)
    {
        var issue = OrganiseText.Describe(item);
        return new OrganiseRow(
            Name:       Path.GetFileName(item.SourcePath),
            Folder:     Path.GetDirectoryName(item.SourcePath) ?? "",
            SourcePath: item.SourcePath,
            Issue:      issue.Length > 0 ? issue : ByteSize.Format(item.Bytes),
            IsProblem:  !item.WillRun,
            WillRun:    item.WillRun,
            ShowFolder: showFolder);
    }

    private static IReadOnlyList<RecentFolder> LoadRecents() =>
    [
        .. RecentDestinations.All().Select(p => new RecentFolder(p, ShortName(p))),
    ];

    /// <summary>The last segment of a path - "Hard Techno" rather than the whole reach up to the
    /// share, which no chip has room for and which is the same for all of them anyway.</summary>
    private static string ShortName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                                 Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : path;
    }

    private void OnProgress(OrganiseProgress p)
    {
        BusyText = p.Verifying
            ? "Checking the copy..."
            : $"{OrganiseText.Verb(Action)}... {(p.CurrentPath is { } c ? Path.GetFileName(c) : "")}".TrimEnd();

        var counted = $"{ByteSize.Count(p.Done)} / {ByteSize.Count(p.Total)}";
        BusyDetail = p.BytesTotal > 0
            ? $"{counted}  -  {ByteSize.Format(p.BytesDone)} of {ByteSize.Format(p.BytesTotal)}"
            : counted;

        BusyProgress = p.Fraction;
    }

    private string ResultText(OrganiseReport report)
    {
        var line = report.ToString();
        if (report.Failures.Count == 0) return line;

        // (!!) Every failure by name, never truncated - a dropped name is a dropped track.
        var named = report.Failures.Select(f => $"{Path.GetFileName(f.Path)}: {f.Message}");
        return line + Environment.NewLine + string.Join(Environment.NewLine, named);
    }

    // -- INotifyPropertyChanged ------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
