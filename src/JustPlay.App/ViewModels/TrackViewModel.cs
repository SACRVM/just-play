using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using JustPlay.Core.Models;

namespace JustPlay.App.ViewModels;

/// <summary>
/// UI wrapper around a <see cref="Track"/>. Metadata and analysis arrive asynchronously;
/// call <see cref="Refresh"/> when the underlying model gains data so bindings update.
/// </summary>
public sealed partial class TrackViewModel : ObservableObject
{
    private Bitmap? _cover;
    private bool _coverResolved;

    public TrackViewModel(Track model) => Model = model;

    public Track Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIndexNumber))]
    [NotifyPropertyChangedFor(nameof(ShowPlayBars))]
    private bool _isCurrent;

    /// <summary>1-based position in the current Tracks list (assigned by the shell VM).</summary>
    [ObservableProperty]
    private int _index;

    /// <summary>Stable insertion sequence (the natural/Explorer drop order). Used to restore the
    /// original order when column sorting is toggled off (third header click).</summary>
    public int AddOrder { get; set; }

    public string Title =>
        Model.Metadata?.DisplayTitle ?? Path.GetFileNameWithoutExtension(Model.FilePath);

    public string Artist =>
        string.IsNullOrWhiteSpace(Model.Metadata?.Artist) ? "—" : Model.Metadata!.Artist!;

    /// <summary>True while a BPM/key/energy analysis pass is in flight for this row — drives the
    /// rotating spinner in the index column (in place of the number / play-bars).</summary>
    public bool IsAnalyzing => Model.AnalysisStatus == AnalysisStatus.Running;

    /// <summary>Whether this track already carries our analysis — drives "Analyze" vs "Re-analyze".</summary>
    public bool HasAnalysis => Model.Analysis is not null;

    /// <summary>There is a detected value to write into tags (so "Write meta tags" has an effect).</summary>
    public bool HasWritableAnalysis =>
        Model.Analysis is { } a && (a.Bpm is > 0 || a.Key is not null || a.Energy is not null);

    /// <summary>There is a detected value for a tag the file is MISSING (so "Fill missing tags" has an
    /// effect). Mirrors the fill-missing branch in MainWindowViewModel.WriteSelectedTags.</summary>
    public bool HasMissingTagToFill =>
        Model.Analysis is { } a &&
        ((a.Bpm is > 0 && Model.Metadata?.TaggedBpm is not > 0) ||
         (a.Key is not null && string.IsNullOrWhiteSpace(Model.Metadata?.TaggedKey)) ||
         (a.Energy is not null && Model.Metadata?.TaggedEnergy is null));

    // Index-column content is mutually exclusive: spinner while analysing, else the 3-bar
    // play indicator on the current track, else the plain position number. (Bindings can't do
    // boolean AND, so the combinations are precomputed here.)
    public bool ShowIndexNumber => !IsCurrent && !IsAnalyzing;
    public bool ShowPlayBars => IsCurrent && !IsAnalyzing;

    public string DurationText =>
        Model.Metadata is { Duration: var d } && d > TimeSpan.Zero
            ? d.ToString(@"m\:ss")
            : "–:––";

    /// <summary>
    /// Numeric BPM — analysed value wins, falls back to the embedded tag.
    /// Null when neither source has it (e.g. analysis still running on a
    /// freshly-dropped file with no BPM tag).
    /// </summary>
    public double? Bpm => Model.Analysis?.Bpm ?? Model.Metadata?.TaggedBpm;

    /// <summary>Analysed BPM if we have it, else whatever the tags claimed.</summary>
    public string BpmText => Bpm is > 0 ? Bpm.Value.ToString("0") : "";

    public string KeyText =>
        Model.Analysis?.Key?.Camelot ?? Model.Metadata?.TaggedKey ?? "";

    public string EnergyText =>
        Model.Analysis?.Energy is int e ? e.ToString() : "";

    public int? Energy => Model.Analysis?.Energy;

    // ── Detected vs. claimed: conflict ("bold") computation ──────────────────
    // The displayed value above is always "detected wins, tag as fallback". A cell is
    // a CONFLICT (rendered bold, right-clickable to write/keep) when our detected value
    // differs from a foreign tag value AND the user hasn't decided yet (Pending). Once
    // they Apply or Keep, the per-field decision in the stored JUSTPLAY blob clears the
    // bold. See memory analysis-tag-persistence-design.

    private TrackAnalysisState? StoredCurrent =>
        Model.Metadata?.StoredAnalysis is { Version: TrackAnalysisState.CurrentVersion } st ? st : null;

    private FieldDecision BpmDecision => StoredCurrent?.BpmDecision ?? FieldDecision.Pending;
    private FieldDecision KeyDecision => StoredCurrent?.KeyDecision ?? FieldDecision.Pending;
    private FieldDecision EnergyDecision => StoredCurrent?.EnergyDecision ?? FieldDecision.Pending;

    public double? DetectedBpm => Model.Analysis?.Bpm;
    public double? ClaimedBpm => Model.Metadata?.TaggedBpm;
    public MusicalKey? DetectedKey => Model.Analysis?.Key;
    public MusicalKey? ClaimedKey => MusicalKey.TryParse(Model.Metadata?.TaggedKey);
    public int? DetectedEnergy => Model.Analysis?.Energy;
    public int? ClaimedEnergy => Model.Metadata?.TaggedEnergy;

    /// <summary>BPM is a conflict only when our value diverges from a claimed BPM and neither a
    /// rounding-equal nor a ½/×2-time relationship explains it (DJs routinely halve/double).</summary>
    public bool BpmConflict =>
        BpmDecision == FieldDecision.Pending
        && DetectedBpm is > 0 && ClaimedBpm is > 0
        && !BpmsMatch(DetectedBpm.Value, ClaimedBpm.Value);

    /// <summary>Key conflict: a claimed key exists and either can't be parsed or maps to a
    /// different Camelot than ours.</summary>
    public bool KeyConflict =>
        KeyDecision == FieldDecision.Pending
        && DetectedKey is { } dk
        && !string.IsNullOrWhiteSpace(Model.Metadata?.TaggedKey)
        && (ClaimedKey is not { } ck || ck.Camelot != dk.Camelot);

    public bool EnergyConflict =>
        EnergyDecision == FieldDecision.Pending
        && DetectedEnergy is { } de && ClaimedEnergy is { } ce && de != ce;

    public bool HasAnyConflict => BpmConflict || KeyConflict || EnergyConflict;

    // A field can be restored to its original when we previously overwrote it (decision Applied)
    // and stashed the foreign value in the blob's Original. Drives the "Restore original" entries.
    public bool CanRestoreBpm => StoredCurrent is { BpmDecision: FieldDecision.Applied, Original.Bpm: > 0 };
    public bool CanRestoreKey => StoredCurrent is { KeyDecision: FieldDecision.Applied } s && s.Original?.Key is not null;
    public bool CanRestoreEnergy => StoredCurrent is { EnergyDecision: FieldDecision.Applied } s && s.Original?.Energy is not null;

    public string RestoreBpmLabel => $"Restore original ({StoredCurrent?.Original?.Bpm:0})";
    public string RestoreKeyLabel => $"Restore original ({StoredCurrent?.Original?.Key?.Camelot})";
    public string RestoreEnergyLabel => $"Restore original ({StoredCurrent?.Original?.Energy})";

    /// <summary>True when the per-field section of the context menu has any entry to show — drives
    /// the separator between the field actions and the bulk actions.</summary>
    public bool HasFieldMenu => HasAnyConflict || CanRestoreBpm || CanRestoreKey || CanRestoreEnergy;

    // Inline "claimed → detected" labels for the context-menu entries (recovers the
    // original-vs-detected info without a popup, per the design).
    public string BpmConflictLabel => $"{ClaimedBpm:0} → {DetectedBpm:0}";
    public string KeyConflictLabel => $"{ClaimedKeyDisplay} → {DetectedKey?.Camelot}";
    public string EnergyConflictLabel => $"{ClaimedEnergy} → {DetectedEnergy}";

    // Full per-field context-menu headers (single-row, divergent cell).
    public string WriteBpmMenu => $"Write BPM   {BpmConflictLabel}";
    public string WriteKeyMenu => $"Write Key   {KeyConflictLabel}";
    public string WriteEnergyMenu => $"Write Energy   {EnergyConflictLabel}";
    public string KeepBpmMenu => $"Keep {ClaimedBpm:0}";
    public string KeepKeyMenu => $"Keep {ClaimedKeyDisplay}";
    public string KeepEnergyMenu => $"Keep {ClaimedEnergy}";

    /// <summary>Claimed key shown in menus: its Camelot if parseable, else the raw tag string.</summary>
    public string ClaimedKeyDisplay =>
        ClaimedKey?.Camelot ?? Model.Metadata?.TaggedKey ?? "?";

    /// <summary>Rounding-equal, or one is ~double/half the other (within 1 BPM).</summary>
    private static bool BpmsMatch(double a, double b)
    {
        double ra = Math.Round(a), rb = Math.Round(b);
        return Math.Abs(ra - rb) <= 1 || Math.Abs(ra - 2 * rb) <= 1 || Math.Abs(2 * ra - rb) <= 1;
    }

    public Bitmap? Cover
    {
        get
        {
            if (_coverResolved) return _cover;
            _coverResolved = true;
            var data = Model.Metadata?.CoverArt;
            if (data is { Length: > 0 })
            {
                try
                {
                    _cover = new Bitmap(new MemoryStream(data));
                    Console.WriteLine($"[Cover OK] {Path.GetFileName(Model.FilePath)} → {data.Length} bytes");
                }
                catch (Exception ex)
                {
                    _cover = null;
                    Console.WriteLine($"[Cover FAIL] {Path.GetFileName(Model.FilePath)} → {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[Cover NONE] {Path.GetFileName(Model.FilePath)} → no embedded picture");
            }
            return _cover;
        }
    }

    /// <summary>Re-evaluate all derived properties after the model gains metadata/analysis.</summary>
    public void Refresh()
    {
        _coverResolved = false;
        OnPropertyChanged(string.Empty); // refresh every binding on this object
    }
}
