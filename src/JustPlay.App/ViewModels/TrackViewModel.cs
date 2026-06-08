using System;
using System.Globalization;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>
    /// Injected by <see cref="MainWindowViewModel"/> so the toggle-favourite
    /// command can run its file I/O there (where the writer and file-release
    /// logic live) without TrackViewModel needing a direct reference to them.
    /// </summary>
    public Func<TrackViewModel, bool, System.Threading.Tasks.Task>? ToggleFavoriteCallback { get; set; }

    public TrackViewModel(Track model) => Model = model;

    public Track Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIndexNumber))]
    [NotifyPropertyChangedFor(nameof(ShowPlayBars))]
    private bool _isCurrent;

    /// <summary>
    /// Whether the track is marked as a favourite. Populated from the POPM tag on
    /// load via <see cref="Refresh"/>; toggled by <see cref="ToggleFavoriteCommand"/>,
    /// which writes the change to the file on a background thread.
    /// </summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>Toggle the favourite flag and persist it to the POPM tag (off the UI thread).
    /// The property is flipped optimistically so the heart responds instantly; the callback
    /// reverts it if the file write fails.</summary>
    [RelayCommand]
    private System.Threading.Tasks.Task ToggleFavorite()
    {
        var newState = !IsFavorite;
        IsFavorite = newState; // optimistic — immediate visual feedback
        if (ToggleFavoriteCallback is { } cb)
            return cb(this, newState);
        return System.Threading.Tasks.Task.CompletedTask;
    }

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

    /// <summary>Genre from the file's tag. Exposed as a VM property (rather than binding the column
    /// directly to <c>Model.Metadata.Genre</c>) so it refreshes with the rest of the row on
    /// <see cref="Refresh"/>. A direct three-level binding through Track/TrackMetadata — neither of
    /// which raises change notifications — only updated lazily when the row was recycled by list
    /// virtualization, so genre appeared to "lazy load" while title/artist showed immediately.</summary>
    public string GenreText => Model.Metadata?.Genre ?? "";

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

    // Detected wins; a foreign tag is shown as Camelot too (parse "Am" -> "8A") so the displayed
    // notation never flips when our analysis lands, falling back to the raw tag if unparseable.
    public string KeyText =>
        Model.Analysis?.Key?.Camelot
        ?? MusicalKey.TryParse(Model.Metadata?.TaggedKey)?.Camelot
        ?? Model.Metadata?.TaggedKey ?? "";

    /// <summary>Detected energy if we have it, else whatever the tag claimed — mirrors
    /// <see cref="Bpm"/>/<see cref="KeyText"/> so all three value columns fill together (from the
    /// tags) in the metadata pass, instead of energy lagging a step behind into the analysis pass.</summary>
    public int? Energy => Model.Analysis?.Energy ?? Model.Metadata?.TaggedEnergy;

    public string EnergyText => Energy is int e ? e.ToString() : "";

    // ── PREP lens: loudness / ReplayGain + beat (danceability) ───────────────
    // These have no standard-tag fallback (the REPLAYGAIN_* fields aren't read into Metadata),
    // so they come from the live analysis OR the stored v5 blob — so a file analysed in a prior
    // session shows its gain/beat immediately on load, like KeyText does from the tags.

    /// <summary>ReplayGain 2.0 track gain in dB (−18 LUFS reference). Null until analysed.</summary>
    public double? ReplayGainDb => Model.Analysis?.ReplayGainDb ?? StoredCurrent?.Detected.ReplayGainDb;

    /// <summary>BS.1770 integrated loudness in LUFS (shown in the GAIN cell's tooltip).</summary>
    public double? LoudnessLufs => Model.Analysis?.LoudnessLufs ?? StoredCurrent?.Detected.LoudnessLufs;

    /// <summary>
    /// The playback loudness target (LUFS) the GAIN column re-references to — set by the shell VM from
    /// the user's Quiet/Normal/Loud level so the column shows the gain ACTUALLY applied, not the −18
    /// tag value. Defaults to −18 (so before the VM sets it, GAIN == the raw tag gain).
    /// </summary>
    public double NormalizationTargetDb { get; set; } = ReplayGain.ReferenceLufs;

    /// <summary>Refresh the GAIN cell after <see cref="NormalizationTargetDb"/> changes (the shell VM
    /// sets the target on every row when the user switches Quiet/Normal/Loud).</summary>
    public void RaiseGainDisplay()
    {
        OnPropertyChanged(nameof(GainText));
        OnPropertyChanged(nameof(GainClips));
        OnPropertyChanged(nameof(GainTooltip));
    }

    /// <summary>Signed gain ACTUALLY applied at the active level (clip-capped), e.g. "−1.0" / "+3.2";
    /// blank if un-analysed. The written REPLAYGAIN tag stays the −18 standard regardless.</summary>
    public string GainText => ReplayGainDb is { } rg
        ? (AppliedGain is { } g && g >= 0.05 ? "+" : "") + (AppliedGain ?? 0).ToString("0.0", CultureInfo.InvariantCulture)
        : "";

    /// <summary>The clip-capped gain applied at <see cref="NormalizationTargetDb"/> (null if un-analysed).</summary>
    private double? AppliedGain =>
        ReplayGainDb is { } rg ? ReplayGain.AppliedGainDb(rg, PeakLinear, NormalizationTargetDb) : null;

    /// <summary>The uncapped gain the track WANTS at the active level (before clip-prevention).</summary>
    private double? DesiredGain =>
        ReplayGainDb is { } rg ? rg + (NormalizationTargetDb - ReplayGain.ReferenceLufs) : null;

    /// <summary>Absolute integrated loudness for the LUFS column, e.g. "−9.5"; blank if none.</summary>
    public string LufsText => LoudnessLufs is { } l ? l.ToString("0.0", CultureInfo.InvariantCulture) : "";

    /// <summary>Linear sample peak (0..~1) from analysis. Not shown directly — drives the clip flag.</summary>
    public double? PeakLinear => Model.Analysis?.Peak ?? StoredCurrent?.Detected.Peak;

    /// <summary>Sample peak in dBFS: 0 = full scale, negative = headroom. −∞ for silence, null un-analysed.</summary>
    private double? PeakDbfs => PeakLinear is { } p ? (p > 0 ? 20.0 * Math.Log10(p) : double.NegativeInfinity) : null;

    /// <summary>True only when a POSITIVE (amplifying) gain is held back by the ceiling at the active
    /// level — i.e. the track wants to be louder to hit the target but the measured peak won't allow it
    /// without clipping (brick-walled, or the gain overflows 0 dBFS). Reds the GAIN cell. A track being
    /// turned DOWN (g ≤ 0) can never clip, so it is never flagged — in an all-brick-walled club library
    /// that keeps red rare and actionable instead of lighting up almost every row.</summary>
    public bool GainClips =>
        PeakDbfs is { } pk && DesiredGain is { } g && g > 0 && (pk >= -0.1 || pk + g > 0);

    /// <summary>Tooltip on the GAIN cell: the clip reason when a positive gain is capped, else the peak
    /// (noting a brick-walled master as plain info — only the capped case is the red "problem").</summary>
    public string GainTooltip
    {
        get
        {
            if (DesiredGain is not { } g || PeakDbfs is not { } pk) return "";
            if (g > 0 && pk + g > 0) return $"Capped: wants +{g:0.0} dB but peak {pk:0.0} dBFS limits it";
            if (pk >= -0.1) return $"Brick-walled master — peak {pk:0.0} dBFS";
            return $"Peak {pk:0.0} dBFS";
        }
    }

    // Danceability (1/α DFA, range ~0..3) — computed and persisted, but NOT shown yet. Reserved for
    // a future "FEEL" lens (MIX │ PREP │ FEEL): it discriminates dance-vs-not, so it clusters high in
    // an all-club library and needs companion groove features before it earns a column.
    /// <summary>Danceability (DFA 1/α, ~0..3) from the beat fingerprint. Reserved for the FEEL lens.</summary>
    public double? Beat =>
        (Model.Analysis?.Fingerprint ?? StoredCurrent?.Detected.Fingerprint) is { } fp ? (double)fp.Danceability : null;

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
        // Sync the favourite flag from the freshly-read metadata (POPM tag).
        // Assign via the generated property so the toolkit's change-notification fires
        // correctly (direct field access triggers MVVMTK0034 and skips the setter path).
        IsFavorite = Model.Metadata?.IsFavorite ?? false;
        OnPropertyChanged(string.Empty); // refresh every binding on this object
    }
}
