using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JustPlay.App.ViewModels;

/// <summary>
/// View-model for the Transfer progress window (ZIP / folder export). It owns the
/// <see cref="CancellationTokenSource"/> for the running export; the window binds the progress bar +
/// count and its two buttons to it. <b>Dismiss</b> just closes the window - the export is a detached
/// Task, not tied to the window's lifetime, so it keeps running. <b>Cancel</b> trips the token, which
/// makes the Core writer abort and delete its partial output.
/// </summary>
public sealed partial class TransferViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Fraction))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Fraction))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _total;

    /// <summary>What is being written, e.g. "Summer Set  (ZIP)" - shown under the title.</summary>
    [ObservableProperty] private string _target = string.Empty;

    /// <summary>The token the export observes; <see cref="Cancel"/> trips it.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>0..1 for the progress bar; 0 until the total is known.</summary>
    public double Fraction => Total > 0 ? (double)Done / Total : 0;

    /// <summary>"24 / 40" once counting, "..." before the first report.</summary>
    public string CountText => Total > 0 ? $"{Done} / {Total}" : "...";

    /// <summary>Push a progress report (called on the UI thread by the Progress&lt;&gt; callback).</summary>
    public void Report(int done, int total)
    {
        Done = done;
        Total = total;
    }

    /// <summary>Abort the export. The Core writer cleans up its half-written archive / folder.</summary>
    public void Cancel() => _cts.Cancel();
}
