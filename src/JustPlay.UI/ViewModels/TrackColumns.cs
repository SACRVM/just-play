using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.UI.Controls;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// Shared column-visibility + sort state for the track tables - the JUST PLAY queue (MaxView), the Pre-Cue
/// Finder file list AND JUST TAG's file pane. ONE definition so the lists can never drift on which columns
/// exist, how the sort arrows render, or how a header click cycles asc -> desc -> off. This class holds no
/// rows: the owning view-model keeps its own collection and re-sorts (via the shared <see cref="TrackSort"/>)
/// when <see cref="SortRequested"/> fires. The shared <see cref="Controls.TrackRow"/> and header bind their
/// cell visibility + sort glyphs straight to this object, so a new column is added in exactly one place.
/// (The anti-drift refactor landed 2026-07-07; the tagging columns joined 2026-08-05 for JUST TAG.)
/// </summary>
public sealed partial class TrackColumns : ObservableObject
{
    // Canonical column ids (lowercase) - the SUPERSET across all track tables. A given list enables a subset:
    // the queue never enables the vibe columns, the finder leans on them, and JUST TAG enables the editorial
    // and file-fact ones the DJ lists have no use for. One superset, three focuses.
    public const string Title = "title", Artist = "artist", Genre = "genre", Bpm = "bpm", Key = "key", Nrg = "nrg",
        Gain = "gain", Lufs = "lufs", Dark = "dark", Hypnotic = "hypnotic", Groove = "groove",
        Punch = "punch", Harsh = "harsh", Comment = "comment", Duration = "duration", Like = "like";

    // The tagging columns (JUST TAG's focus). What you can filter on, you must be able to see:
    // the search can aim at a cover, an ID3 version or a file type, so the table has to be able
    // to show those too, or a hit is a claim you cannot check.
    public const string Album = "album", AlbumArtist = "albumartist", Year = "year", TrackNo = "trackno",
        Cover = "cover", Id3 = "id3", FileType = "filetype", FileName = "filename",
        // The traffic light: is OUR analysis current, a version behind, or absent? JUST TAG is where
        // you check and re-trigger it, so it earns a column of its own.
        Analysis = "analysis";

    private HashSet<string> _enabled;

    public TrackColumns(IEnumerable<string> enabled) =>
        _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);

    /// <summary>The currently-visible column ids (read-only view for persistence).</summary>
    public IReadOnlyCollection<string> Enabled => _enabled;

    /// <summary>
    /// The content-sized widths of the flexible TEXT columns for THIS table. It lives here because
    /// one table owns one <see cref="TrackColumns"/>, which is exactly the scope the sizing is meant
    /// to have - per view, not per app. The row and header bind through it, so a
    /// host needs no extra plumbing; it only has to call
    /// <see cref="TrackColumnWidths.Measure"/> when its listing changes.
    /// </summary>
    public TrackColumnWidths Widths { get; } = new();

    /// <summary>
    /// Re-share the row's width between the text columns. <paramref name="rowWidth"/> is the whole
    /// row; the fixed columns are subtracted here so no caller has to know what they cost.
    /// </summary>
    public void FitWidths(double rowWidth) =>
        Widths.Fit(rowWidth - TrackTableMetrics.FixedTotal(this), this);

    /// <summary>Swap the whole visible-column set. The queue calls this when its A/B/C lens changes; the
    /// finder has a single set and mutates it via <see cref="ToggleColumnCommand"/>.</summary>
    public void SetEnabled(IEnumerable<string> enabled)
    {
        _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        RaiseVisibility();
    }

    /// <summary>Is this column id switched on? The Show* properties below are the bindable form of
    /// the same question; this one takes the id, for code that walks a set of columns
    /// (<see cref="TrackColumnWidths"/> sizing the flexible text ones).</summary>
    public bool IsEnabled(string columnId) => _enabled.Contains(columnId);

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

    // -- Sort state (identical cycle in both lists; each owner sorts its own collection) -----------------
    [ObservableProperty] private string? _sortColumn;
    [ObservableProperty] private bool _sortDescending;

    /// <summary>Raised when the sort column/direction changed - the owner re-sorts its collection.</summary>
    public event Action? SortRequested;

    /// <summary>
    /// The little arrow next to the header of the column being sorted - <see cref="IconKind.None"/>
    /// for every other column, which is also what hides it (<c>IconConverters.IsSet</c>).
    ///
    /// <para>(!!) It used to return the characters "^" / "v". Icons in this suite are shipped vectors,
    /// never glyphs, because the OS picks the font and we do not - see CLAUDE.md rule 5. The type is
    /// <see cref="IconKind"/> rather than a string precisely so the compiler finds every header that
    /// still expects text: there are two dozen of them across four windows.</para>
    /// </summary>
    private IconKind Glyph(string col) =>
        !string.Equals(SortColumn, col, StringComparison.OrdinalIgnoreCase) ? IconKind.None
        : SortDescending ? IconKind.SortDesc
        : IconKind.SortAsc;

    public IconKind TitleSortGlyph    => Glyph(Title);
    public IconKind ArtistSortGlyph   => Glyph(Artist);
    public IconKind AlbumSortGlyph    => Glyph(Album);
    public IconKind AlbumArtistSortGlyph => Glyph(AlbumArtist);
    public IconKind GenreSortGlyph    => Glyph(Genre);
    public IconKind YearSortGlyph     => Glyph(Year);
    public IconKind TrackNoSortGlyph  => Glyph(TrackNo);
    public IconKind BpmSortGlyph      => Glyph(Bpm);
    public IconKind KeySortGlyph      => Glyph(Key);
    public IconKind NrgSortGlyph      => Glyph(Nrg);
    public IconKind GainSortGlyph     => Glyph(Gain);
    public IconKind LufsSortGlyph     => Glyph(Lufs);
    public IconKind DarkSortGlyph     => Glyph(Dark);
    public IconKind HypnoticSortGlyph => Glyph(Hypnotic);
    public IconKind GrooveSortGlyph   => Glyph(Groove);
    public IconKind PunchSortGlyph    => Glyph(Punch);
    public IconKind HarshSortGlyph    => Glyph(Harsh);
    public IconKind CommentSortGlyph  => Glyph(Comment);
    public IconKind AnalysisSortGlyph => Glyph(Analysis);
    public IconKind CoverSortGlyph    => Glyph(Cover);
    public IconKind Id3SortGlyph      => Glyph(Id3);
    public IconKind FileTypeSortGlyph => Glyph(FileType);
    public IconKind DurationSortGlyph => Glyph(Duration);
    public IconKind LikeSortGlyph     => Glyph(Like);
    public IconKind FileNameSortGlyph => Glyph(FileName);

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

    /// <summary>Header click: same column ascending -> descending -> off (natural order); a new column starts
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
