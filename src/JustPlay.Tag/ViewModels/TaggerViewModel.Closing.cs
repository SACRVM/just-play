namespace JustPlay.Tag.ViewModels;

/// <summary>
/// Closing the window while files are being written. The rules are <see cref="CloseSequence"/>'s;
/// this part owns the state the window shows while it waits.
/// </summary>
public sealed partial class TaggerViewModel
{
    private CloseSequence? _closing;

    /// <summary>What closing has to settle first - built over this view model's own state.</summary>
    public CloseSequence Closing => _closing ??= new CloseSequence
    {
        Writes = _writes,
        Analysing = () => IsAnalysing,
        CancelAnalysis = CancelAnalysis,
        AnalysisRun = () => _analysisRun,
        EditorSaving = () => EditorSaving,
        EditorDirty = () => Editor.IsDirty,
        Finishing = on => IsFinishing = on,
    };

    /// <summary>
    /// Whether the shared editor is in the middle of a multi-file save. That state belongs to the
    /// editor - the save runs inside <c>TagEditorViewModel.SaveManyAsync</c> - and closing reads it
    /// so the unsaved-changes prompt is held back while that save runs: the prompt's Save would start
    /// a second one on top of it.
    /// </summary>
    private bool EditorSaving => Editor.IsSaving;

    private bool _finishing;

    /// <summary>
    /// The window is closing and waiting for the file being written. Drives the shared busy overlay
    /// and locks the panes, so nothing new is started under a window that is on its way out.
    /// </summary>
    public bool IsFinishing
    {
        get => _finishing;
        private set
        {
            Set(ref _finishing, value);
            Raise(nameof(CanAnalyze));
            RaiseBusy();
        }
    }

    // -- The shared overlay ------------------------------------------------------------------------
    //
    // ONE overlay, two reasons to show it: the analyze run, and closing while a write finishes. The
    // close wins the text - it is the newer news, and the run it may be waiting for has been told to
    // stop already.

    /// <summary>The overlay is up.</summary>
    public bool ShowBusy => IsAnalysing || IsFinishing;

    /// <summary>The overlay's phase line.</summary>
    public string? BusyMessage => IsFinishing ? "Finishing the current file" : AnalysisMessage;

    /// <summary>The overlay's second line.</summary>
    public string? BusyDetail => IsFinishing ? "The window closes when it is written" : AnalysisDetail;

    /// <summary>The overlay's ring - travelling while closing, because how long one file takes to
    /// finish is not something we can count.</summary>
    public double? BusyProgress => IsFinishing ? null : AnalysisProgress;

    private void RaiseBusy()
    {
        foreach (var n in (string[])[nameof(ShowBusy), nameof(BusyMessage), nameof(BusyDetail),
                                     nameof(BusyProgress)])
            Raise(n);
        // The busy disc and the empty note share the pane's centre - see ShowEmptyNote.
        RaiseEmptyState();
    }
}
