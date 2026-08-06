using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// Shared column-visibility + sort state for the track tables — the JUST PLAY queue (MaxView), the Pre-Cue
/// Finder file list AND JUST TAG's file pane. ONE definition so the lists can never drift on which columns
/// exist, how the sort arrows render, or how a header click cycles asc → desc → off. This class holds no
/// rows: the owning view-model keeps its own collection and re-sorts (via the shared <see cref="TrackSort"/>)
/// when <see cref="SortRequested"/> fires. The shared <see cref="Controls.TrackRow"/> and header bind their
/// cell visibility + sort glyphs straight to this object, so a new column is added in exactly one place.
/// (Chloe 2026-07-07 — the anti-drift refactor; 2026-08-05 the tagging columns joined for JUST TAG.)
/// </summary>
public sealed partial class TrackColumns : ObservableObject
{
    // Canonical column ids (lowercase) — the SUPERSET across all track tables. A given list enables a subset:
    // the queue never enables the vibe columns, the finder leans on them, and JUST TAG enables the editorial
    // and file-fact ones the DJ lists have no use for. One superset, three focuses.
    public const string Title = "title", Artist = "artist", Genre = "genre", Bpm = "bpm", Key = "key", Nrg = "nrg",
        Gain = "gain", Lufs = "lufs", Dark = "dark", Hypnotic = "hypnotic", Groove = "groove",
        Punch = "punch", Harsh = "harsh", Comment = "comment", Duration = "duration", Like = "like";

    // The tagging columns (JUST TAG's focus). "What you can filter on, you must be able to see" —
    // Chloe 2026-08-05: the search can aim at a cover, an ID3 version or a file type, so the table has
    // to be able to show those too, or a hit is a claim you cannot check.
    public const string Album = "album", AlbumArtist = "albumartist", Year = "year", TrackNo = "trackno",
        Cover = "cover", Id3 = "id3", FileType = "filetype", FileName = "filename",
        // The traffic light: is OUR analysis current, a version behind, or absent? JUST TAG is where
        // you check and re-trigger it, so it earns a column of its own (Chloe 2026-08-05).
        Analysis = "analysis";

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
    public bool ShowAlbum    => _enabled.Contains(Album);
    public bool ShowAlbumArtist => _enabled.Contains(AlbumArtist);
    public bool ShowGenre    => _enabled.Contains(Genre);
    public bool ShowYear     => _enabled.Contains(Year);
    public bool ShowTrackNo  => _enabled.Contains(TrackNo);
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
    public bool ShowAnalysis => _enabled.Contains(Analysis);
    public bool ShowCover    => _enabled.Contains(Cover);
    public bool ShowId3      => _enabled.Contains(Id3);
    public bool ShowFileType => _enabled.Contains(FileType);
    public bool ShowDuration => _enabled.Contains(Duration);
    public bool ShowLike     => _enabled.Contains(Like);
    public bool ShowFileName => _enabled.Contains(FileName);

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
        OnPropertyChanged(nameof(ShowAlbum));
        OnPropertyChanged(nameof(ShowAlbumArtist));
        OnPropertyChanged(nameof(ShowGenre));
        OnPropertyChanged(nameof(ShowYear));
        OnPropertyChanged(nameof(ShowTrackNo));
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
        OnPropertyChanged(nameof(ShowAnalysis));
        OnPropertyChanged(nameof(ShowCover));
        OnPropertyChanged(nameof(ShowId3));
        OnPropertyChanged(nameof(ShowFileType));
        OnPropertyChanged(nameof(ShowDuration));
        OnPropertyChanged(nameof(ShowLike));
        OnPropertyChanged(nameof(ShowFileName));
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
    public string AlbumSortGlyph    => Glyph(Album);
    public string AlbumArtistSortGlyph => Glyph(AlbumArtist);
    public string GenreSortGlyph    => Glyph(Genre);
    public string YearSortGlyph     => Glyph(Year);
    public string TrackNoSortGlyph  => Glyph(TrackNo);
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
    public string AnalysisSortGlyph => Glyph(Analysis);
    public string CoverSortGlyph    => Glyph(Cover);
    public string Id3SortGlyph      => Glyph(Id3);
    public string FileTypeSortGlyph => Glyph(FileType);
    public string DurationSortGlyph => Glyph(Duration);
    public string LikeSortGlyph     => Glyph(Like);
    public string FileNameSortGlyph => Glyph(FileName);

    partial void OnSortColumnChanged(string? value) => RaiseSortGlyphs();
    partial void OnSortDescendingChanged(bool value) => RaiseSortGlyphs();

    private void RaiseSortGlyphs()
    {
        OnPropertyChanged(nameof(TitleSortGlyph));
        OnPropertyChanged(nameof(ArtistSortGlyph));
        OnPropertyChanged(nameof(AlbumSortGlyph));
        OnPropertyChanged(nameof(AlbumArtistSortGlyph));
        OnPropertyChanged(nameof(GenreSortGlyph));
        OnPropertyChanged(nameof(YearSortGlyph));
        OnPropertyChanged(nameof(TrackNoSortGlyph));
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
        OnPropertyChanged(nameof(AnalysisSortGlyph));
        OnPropertyChanged(nameof(CoverSortGlyph));
        OnPropertyChanged(nameof(Id3SortGlyph));
        OnPropertyChanged(nameof(FileTypeSortGlyph));
        OnPropertyChanged(nameof(DurationSortGlyph));
        OnPropertyChanged(nameof(LikeSortGlyph));
        OnPropertyChanged(nameof(FileNameSortGlyph));
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
