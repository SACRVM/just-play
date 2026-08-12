using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Library;
using JustPlay.Metadata;
using JustPlay.UI.Logging;
using JustPlay.UI.ViewModels;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// JUST TAG runs the analyzer.
///
/// <para><b>What was broken.</b> The file table has carried the analysis traffic light (current /
/// older / never analysed) since the shared row landed, and the app offered no way to act on it. A
/// meter you cannot act on is worse than no meter: it tells you something is wrong and then sends
/// you to another app to fix it. This is that action.</para>
///
/// <para><b>Nothing here is a second dialect of "analyse these files".</b> Every moving part is the
/// suite's:</para>
/// <list type="bullet">
/// <item><description>the DSP is <c>ITrackAnalysisService</c> - the same orchestrator JUST PLAY's DI
/// and the CLI's <c>EngineComposer</c> build, over the same detectors (see Program.cs);</description></item>
/// <item><description>the batching is <see cref="AnalysisBatchRunner"/> + <see cref="AnalysisQueue"/>
/// from JustPlay.Library - bounded workers, pausable, cancellable, and a cancelled run RETURNS its
/// counts instead of throwing them away;</description></item>
/// <item><description>the write is <see cref="AnalysisTagWrite.ForDetected"/> (the blob + the
/// standard tags + the decision stamps, defined once for the whole suite) handed to the SAME
/// <see cref="TagWriteExecutor"/> the tag editor saves through, so a file being previewed is
/// released first instead of failing on an open handle;</description></item>
/// <item><description>the rows repaint through <see cref="RefreshTagsFor"/>, the refresh the
/// TRANSFORM window already uses;</description></item>
/// <item><description>progress is the shared floating <c>BusyOverlay</c> - never inline progress
/// that pushes the list around (memory <c>progress-overlay-shared</c>).</description></item>
/// </list>
/// </summary>
public sealed partial class TaggerViewModel
{
    private readonly ITrackAnalysisService _analysis;
    private readonly IMetadataWriter _writer;
    private readonly TagWriteExecutor _execute;

    private CancellationTokenSource? _analysisCts;

    /// <summary>The suite's "do not swallow errors silently" channel (shared LogWindow). Every file
    /// the analyzer could not read is named here, by name, and the pane's summary line opens it.</summary>
    public LogViewModel Log { get; }

    // -- What the menu says ----------------------------------------------------------------------

    /// <summary>
    /// "Analyze" until the selection has been analysed, then "Re-analyze" - JUST PLAY's queue menu
    /// wording, carried over verbatim rather than invented here, and with the same "(N)" count every
    /// other bulk entry in this menu carries.
    /// </summary>
    public string AnalyzeMenuHeader
    {
        get
        {
            var verb = _selected.Count > 0 && _selected.All(r => r.Track.HasAnalysis)
                ? "Re-analyze" : "Analyze";
            return _selected.Count > 1 ? $"{verb} ({_selected.Count})" : verb;
        }
    }

    /// <summary>One batch at a time. A second run while one is going would fight it for the same
    /// files and for the same overlay.</summary>
    public bool CanAnalyze => _selected.Count > 0 && !IsAnalysing;

    // -- The run ---------------------------------------------------------------------------------

    private bool _analysing;

    /// <summary>A batch is in flight - drives the shared floating overlay and the STOP button.</summary>
    public bool IsAnalysing
    {
        get => _analysing;
        private set
        {
            Set(ref _analysing, value);
            Raise(nameof(CanAnalyze));
            Raise(nameof(HasAnalysisNote));
            // The busy disc and the empty note share the pane's centre - see ShowEmptyNote.
            RaiseEmptyState();
        }
    }

    private string? _analysisMessage;
    /// <summary>The overlay's phase line ("Analysing", "Waiting for playback", "Paused").</summary>
    public string? AnalysisMessage { get => _analysisMessage; private set => Set(ref _analysisMessage, value); }

    private string? _analysisDetail;
    /// <summary>The overlay's counter ("12 / 30").</summary>
    public string? AnalysisDetail { get => _analysisDetail; private set => Set(ref _analysisDetail, value); }

    private double? _analysisProgress;
    /// <summary>0..1 for the overlay's ring, or null while there is nothing to divide by.</summary>
    public double? AnalysisProgress { get => _analysisProgress; private set => Set(ref _analysisProgress, value); }

    private string? _analysisNote;

    /// <summary>
    /// What the last run did, in one line, left standing in the FILES header afterwards. It is the
    /// only part of the report that survives on screen, so it always states the counts - a run that
    /// quietly vanished when it finished would leave "did that work?" unanswered.
    /// </summary>
    public string? AnalysisNote { get => _analysisNote; private set { Set(ref _analysisNote, value); Raise(nameof(HasAnalysisNote)); } }

    public bool HasAnalysisNote => !string.IsNullOrEmpty(_analysisNote) && !IsAnalysing;

    private bool _analysisFailed;

    /// <summary>The last run left files behind. Colours the note and is what makes it clickable -
    /// the names are in the log.</summary>
    public bool AnalysisFailed { get => _analysisFailed; private set => Set(ref _analysisFailed, value); }

    /// <summary>
    /// Analyse these files and write what we measure into them.
    ///
    /// <para>(!) There is NO freshness gate: a file that already carries a current blob is analysed
    /// again, because you asked for THESE files. Silently skipping the ones we think are fine would
    /// make "re-analyse" a no-op on exactly the tracks somebody re-analyses deliberately.</para>
    /// </summary>
    public async Task AnalyzeAsync(IReadOnlyList<FileRow> rows)
    {
        if (IsAnalysing || rows is not { Count: > 0 }) return;

        // Path -> row, so a worker can find the row it just finished without walking the listing.
        var byPath = rows
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _analysisCts?.Dispose();
        var cts = _analysisCts = new CancellationTokenSource();

        IsAnalysing = true;
        AnalysisNote = null;
        AnalysisFailed = false;
        AnalysisMessage = "Analysing";
        AnalysisDetail = $"0 / {byPath.Count}";
        AnalysisProgress = 0;

        // The AN column's spinner, immediately - a row that is queued for a long batch must not sit
        // there looking untouched.
        foreach (var row in byPath.Values)
        {
            row.Track.Model.AnalysisStatus = AnalysisStatus.Running;
            row.Track.Refresh();
        }

        var runner = new AnalysisBatchRunner(
            analyse: (path, ct) => AnalyseOneAsync(byPath, path, ct),
            // (!) The gig-safe gate is deliberately OPEN here. JUST PLAY closes it while a track is
            // playing or the stream is on air, because it is the tool that performs; JUST TAG never
            // is. Its preview is auditioning, the executor releases a previewed file before writing
            // it, and a gate that closed on the preview would stall the batch with the counter
            // frozen - which reads as a hang, not as courtesy.
            mayWorkNow: () => true,
            options: new AnalysisBatchOptions { MaxConcurrency = Threads },
            // No stillNeedsAnalysis: see the summary above - an explicit selection is final.
            log: line => Log.Append(line));

        // Constructed on the UI thread, so its callbacks come back to the UI thread.
        var progress = new Progress<AnalysisBatchProgress>(OnAnalysisProgress);

        AnalysisBatchReport report;
        try
        {
            report = await runner.RunAsync(byPath.Keys.ToList(), progress, cts.Token)
                                 .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The runner reports a cancelled run rather than throwing, so anything arriving here is
            // a genuine surprise. It must not take the app down mid-batch.
            Log.Append($"Analysis run failed: {ex.Message}");
            IsAnalysing = false;
            AnalysisFailed = true;
            AnalysisNote = "Analysis stopped - see the log.";
            Raise(nameof(HasAnalysisNote));
            ClearRunningRows(byPath.Values);
            return;
        }
        finally
        {
            AnalysisMessage = null;
            AnalysisDetail = null;
            AnalysisProgress = null;
        }

        IsAnalysing = false;
        ClearRunningRows(byPath.Values);

        // (!!) Every failure by name, never a count on its own - a dropped name is a dropped track
        // (memory never-leave-songs-behind). The same shape the ORGANISE window reports in.
        foreach (var failure in report.Failures)
            Log.Append($"Analyze failed - \"{Path.GetFileName(failure.Path)}\": {failure.Message}");

        AnalysisFailed = report.Failed > 0;
        AnalysisNote = AnalysisRunText.Summarise(report);
        Raise(nameof(HasAnalysisNote));
        Log.Append(AnalysisNote);

        // Silence on success, loud on failure: the log opens itself only when files were left
        // behind, because that is the case where a line in a header is not enough.
        if (report.Failed > 0) AnalysisFailuresReported?.Invoke();
    }

    /// <summary>Raised once, at the end of a run that could not analyse everything. The window opens
    /// the shared log window on it - the view model owns no windows.</summary>
    public event Action? AnalysisFailuresReported;

    /// <summary>
    /// Stop after the files in flight. What has already been analysed stays analysed: each file is
    /// written the moment its DSP finishes, so a cancelled run is not half a transaction - it is
    /// simply a shorter one, and the rows that finished are green.
    /// </summary>
    public void CancelAnalysis()
    {
        try { _analysisCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the run finished between the click and here */ }
    }

    // -- One file --------------------------------------------------------------------------------

    private async Task AnalyseOneAsync(
        IReadOnlyDictionary<string, FileRow> rows, string path, CancellationToken ct)
    {
        var detected = await _analysis.AnalyzeAsync(path, null, ct).ConfigureAwait(false);

        // The DSP ran HERE - the one honest timestamp for this measurement.
        var analysedAt = DateTime.UtcNow;

        // (!) NO cancellation check between the decode and the write. Cancellation stops new files
        // from STARTING and aborts a decode in flight; a file whose DSP already finished is written,
        // because the expensive part is spent and throwing the result away would mean STOP quietly
        // undid work that was done. "Whatever finished stays finished" is the promise the STOP
        // button makes, and this is where it is kept.

        // (!) Read the FILE, not the row. A row hydrated from the library index has tags but no
        // blob (TrackIndexMapping.ToMetadata stores no StoredAnalysis), and the blob is what carries
        // the per-field KEPT decisions. Deciding the write off the row would therefore overwrite a
        // key the user had hand-corrected, on exactly the folders the index covers. One extra file
        // open against a whole decode is nothing.
        TrackMetadata? current = null;
        try { current = _reader.Read(path); }
        catch (Exception) { /* unreadable tags: write as if the file carried none */ }

        // The suite's ONE composition of "what our detected values put in a file" - blob + standard
        // tags + ReplayGain + the decision stamps, and it leaves a KEPT field alone.
        var write = AnalysisTagWrite.ForDetected(detected, current, analysedAt);

        if (write is not null)
        {
            // The SAME executor the editor saves through: it releases the file if the preview is
            // holding it, then writes. A throw here is caught by the runner, counted, and the file
            // is named in the report - the batch keeps going.
            _execute(path, p => _writer.Write(p, write));
        }

        // The row reads itself back off disk through the existing refresh - so the AN light, BPM,
        // KEY and NRG all come from the file that was actually written, not from what we think we
        // wrote. Posted, because RefreshTagsFor drives the UI-thread repaint drip.
        if (rows.ContainsKey(path))
            Dispatcher.UIThread.Post(() => RefreshTagsFor([path]));
    }

    /// <summary>
    /// Any row still showing the spinner when the run ends - skipped, cancelled, or failed - is put
    /// back to a state that describes it. A row left Running spins for ever and claims work that
    /// nobody is doing.
    /// </summary>
    private static void ClearRunningRows(IEnumerable<FileRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Track.Model.AnalysisStatus != AnalysisStatus.Running) continue;
            row.Track.Model.AnalysisStatus =
                row.Track.Model.Analysis is null ? AnalysisStatus.Pending : AnalysisStatus.Done;
            row.Track.Refresh();
        }
    }

    private void OnAnalysisProgress(AnalysisBatchProgress p)
    {
        AnalysisMessage = p.Phase switch
        {
            AnalysisPhase.Paused    => "Paused",
            AnalysisPhase.Yielding  => "Waiting",
            AnalysisPhase.Done      => "Finishing",
            _                       => "Analysing",
        };
        AnalysisDetail = $"{p.Done} / {p.Total}";
        AnalysisProgress = p.Fraction;
    }
}

/// <summary>
/// What a finished run says in one line. Pure and separate so the copy is pinned by a test rather
/// than read off a screenshot - the same reason <see cref="TagSearch"/> lives on its own.
/// </summary>
public static class AnalysisRunText
{
    /// <summary>
    /// The header line for a completed (or stopped) batch. It always names the counts, and it never
    /// rounds a failure away: "3 failed" is the part that has to survive whatever else is true.
    /// </summary>
    public static string Summarise(AnalysisBatchReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var parts = new List<string>(3);

        parts.Add(report.Succeeded == 1 ? "1 file analysed" : $"{report.Succeeded} files analysed");
        if (report.Failed > 0) parts.Add($"{report.Failed} failed");
        if (report.Cancelled && report.Remaining > 0) parts.Add($"stopped, {report.Remaining} left");

        var line = string.Join(" - ", parts);

        // Only once there is enough of it to be worth reading. Under a minute the seconds are the
        // useful unit; above it, minutes - "0.0 min" is not an answer to "how long did that take".
        var elapsed = report.Elapsed;
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            var time = elapsed < TimeSpan.FromMinutes(1)
                ? $"{elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s"
                : $"{elapsed.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture)} min";
            line += $" in {time}";
        }

        return line;
    }
}
