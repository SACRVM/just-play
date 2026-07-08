using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JustPlay.App.ViewModels;

/// <summary>
/// Shared column-visibility + sort state for the track tables — the JUST PLAY queue (MaxView) AND the
/// Pre-Cue Finder file list. ONE definition so the two lists can never drift on which columns exist, how the
/// sort arrows render, or how a header click cycles asc → desc → off. This class holds no rows: the owning
/// view-model keeps its own collection + comparer and re-sorts when <see cref="SortRequested"/> fires. The
/// shared <see cref="Controls.TrackRow"/> and header bind their cell visibility + sort glyphs straight to
/// this object, so a new column is added in exactly one place. (Chloe 2026-07-07 — the anti-drift refactor.)
/// </summary>
public sealed partial class TrackColumns : ObservableObject
{
    // Canonical column ids (lowercase) — the SUPERSET across all track tables. A given list enables a subset;
    // the queue never enables the vibe columns, the finder can enable all of them.
    public const string Title = "title", Artist = "artist", Genre = "genre", Bpm = "bpm", Key = "key", Nrg = "nrg",
        Gain = "gain", Lufs = "lufs", Dark = "dark", Hypnotic = "hypnotic", Groove = "groove",
        Punch = "punch", Harsh = "harsh", Comment = "comment", Duration = "duration", Like = "like";

    private HashSet<string> _enabled;

    public TrackColumns(IEnumerable<string> enabled) =>
        _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);

    /// <summary>The currently-visible column ids (read-only view for persistence).</summary>
    public IReadOnlyCollection<string> Enabled => _enabled;

    /// <summary>Swap the whole visible-column set. The queue calls this when its A/B/C lens changes; the
    /// finder has a single set and mutates it via <see cref="ToggleColumnCommand"/>.</summary>
    public void SetEnabled(IEnumerable<string> enabled)
    {
        _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        RaiseVisibility();
    }

    public bool ShowArtist   => _enabled.Contains(Artist);
    public bool ShowGenre    => _enabled.Contains(Genre);
    public bool ShowBpm      => _enabled.Contains(Bpm);
    public bool ShowKey      => _enabled.Contains(Key);
    public bool ShowNrg      => _enabled.Contains(Nrg);
    public bool ShowGain     => _enabled.Contains(Gain);
    public bool ShowLufs     => _enabled.Contains(Lufs);
    public bool ShowDark     => _enabled.Contains(Dark);
    public bool ShowHypnotic => _enabled.Contains(Hypnotic);
    public bool ShowGroove   => _enabled.Contains(Groove);
    public bool ShowPunch    => _enabled.Contains(Punch);
    public bool ShowHarsh    => _enabled.Contains(Harsh);
    public bool ShowComment  => _enabled.Contains(Comment);
    public bool ShowDuration => _enabled.Contains(Duration);
    public bool ShowLike     => _enabled.Contains(Like);

    /// <summary>Fires after the visible set changes so the owner can persist it.</summary>
    public event Action? VisibilityChanged;

    /// <summary>Toggle a single column id in the current set (the finder's flat single-set model). The queue
    /// routes toggles through its own A/B/C-aware command instead and pushes the result via SetEnabled.</summary>
    [RelayCommand]
    private void ToggleColumn(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!_enabled.Remove(id)) _enabled.Add(id);
        RaiseVisibility();
        VisibilityChanged?.Invoke();
    }

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowArtist));
        OnPropertyChanged(nameof(ShowGenre));
        OnPropertyChanged(nameof(ShowBpm));
        OnPropertyChanged(nameof(ShowKey));
        OnPropertyChanged(nameof(ShowNrg));
        OnPropertyChanged(nameof(ShowGain));
        OnPropertyChanged(nameof(ShowLufs));
        OnPropertyChanged(nameof(ShowDark));
        OnPropertyChanged(nameof(ShowHypnotic));
        OnPropertyChanged(nameof(ShowGroove));
        OnPropertyChanged(nameof(ShowPunch));
        OnPropertyChanged(nameof(ShowHarsh));
        OnPropertyChanged(nameof(ShowComment));
        OnPropertyChanged(nameof(ShowDuration));
        OnPropertyChanged(nameof(ShowLike));
    }

    // ── Sort state (identical cycle in both lists; each owner sorts its own collection) ─────────────────
    [ObservableProperty] private string? _sortColumn;
    [ObservableProperty] private bool _sortDescending;

    /// <summary>Raised when the sort column/direction changed — the owner re-sorts its collection.</summary>
    public event Action? SortRequested;

    private string Glyph(string col) =>
        string.Equals(SortColumn, col, StringComparison.OrdinalIgnoreCase) ? (SortDescending ? "▼" : "▲") : "";

    public string TitleSortGlyph    => Glyph(Title);
    public string ArtistSortGlyph   => Glyph(Artist);
    public string GenreSortGlyph    => Glyph(Genre);
    public string BpmSortGlyph      => Glyph(Bpm);
    public string KeySortGlyph      => Glyph(Key);
    public string NrgSortGlyph      => Glyph(Nrg);
    public string GainSortGlyph     => Glyph(Gain);
    public string LufsSortGlyph     => Glyph(Lufs);
    public string DarkSortGlyph     => Glyph(Dark);
    public string HypnoticSortGlyph => Glyph(Hypnotic);
    public string GrooveSortGlyph   => Glyph(Groove);
    public string PunchSortGlyph    => Glyph(Punch);
    public string HarshSortGlyph    => Glyph(Harsh);
    public string CommentSortGlyph  => Glyph(Comment);
    public string DurationSortGlyph => Glyph(Duration);
    public string LikeSortGlyph     => Glyph(Like);

    partial void OnSortColumnChanged(string? value) => RaiseSortGlyphs();
    partial void OnSortDescendingChanged(bool value) => RaiseSortGlyphs();

    private void RaiseSortGlyphs()
    {
        OnPropertyChanged(nameof(TitleSortGlyph));
        OnPropertyChanged(nameof(ArtistSortGlyph));
        OnPropertyChanged(nameof(GenreSortGlyph));
        OnPropertyChanged(nameof(BpmSortGlyph));
        OnPropertyChanged(nameof(KeySortGlyph));
        OnPropertyChanged(nameof(NrgSortGlyph));
        OnPropertyChanged(nameof(GainSortGlyph));
        OnPropertyChanged(nameof(LufsSortGlyph));
        OnPropertyChanged(nameof(DarkSortGlyph));
        OnPropertyChanged(nameof(HypnoticSortGlyph));
        OnPropertyChanged(nameof(GrooveSortGlyph));
        OnPropertyChanged(nameof(PunchSortGlyph));
        OnPropertyChanged(nameof(HarshSortGlyph));
        OnPropertyChanged(nameof(CommentSortGlyph));
        OnPropertyChanged(nameof(DurationSortGlyph));
        OnPropertyChanged(nameof(LikeSortGlyph));
    }

    /// <summary>Header click: same column ascending → descending → off (natural order); a new column starts
    /// ascending. Fires <see cref="SortRequested"/> so the owner applies it to its own rows.</summary>
    public void SortBy(string? column)
    {
        if (string.IsNullOrEmpty(column)) return;
        if (string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            if (!SortDescending) SortDescending = true;
            else { SortColumn = null; SortDescending = false; }
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }
        SortRequested?.Invoke();
    }
}
