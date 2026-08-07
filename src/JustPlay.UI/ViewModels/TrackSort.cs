using System;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// How a track table sorts - ONE comparator for every list in the suite (the JUST PLAY queue, the
/// PRE CUE FINDER, JUST TAG). The column ids are <see cref="TrackColumns"/>'s constants, so a column
/// that exists can be sorted by, everywhere, without a third copy of this switch.
///
/// <para>It was two copies (MainWindowViewModel.ApplySort and PreCueFinderViewModel.SortList) that had
/// already drifted - the queue read the genre off <c>Model.Metadata</c> while the finder read
/// <c>GenreText</c>. Same result today; the kind of difference that stops being the same result the
/// moment one of them gains a fallback. Merged here 2026-08-05.</para>
///
/// <para>Text compares naturally ("track2" &lt; "track10"), numbers as nullable numbers so an
/// un-analysed row sorts as "no value" rather than as zero. The caller owns the direction and the
/// "unsorted -> restore load order" case: this only answers "which of these two comes first".</para>
/// </summary>
public static class TrackSort
{
    /// <summary>Compare two rows by a column id. Ascending; an unknown id compares equal, which
    /// leaves the caller's list in its current order rather than scrambling it.</summary>
    public static int Compare(TrackViewModel a, TrackViewModel b, string? column) => column switch
    {
        TrackColumns.Title       => Text(a.Title, b.Title),
        TrackColumns.Artist      => Text(a.Artist, b.Artist),
        TrackColumns.Album       => Text(a.AlbumText, b.AlbumText),
        TrackColumns.AlbumArtist => Text(a.AlbumArtistText, b.AlbumArtistText),
        TrackColumns.Genre       => Text(a.GenreText, b.GenreText),
        TrackColumns.Year        => Nullable.Compare(a.Year, b.Year),
        TrackColumns.TrackNo     => Nullable.Compare(a.TrackNo, b.TrackNo),
        TrackColumns.Key         => Text(a.KeyText, b.KeyText),
        TrackColumns.Bpm         => Nullable.Compare(a.Bpm, b.Bpm),
        TrackColumns.Nrg         => Nullable.Compare(a.Energy, b.Energy),
        TrackColumns.Gain        => Nullable.Compare(a.ReplayGainDb, b.ReplayGainDb),
        TrackColumns.Lufs        => Nullable.Compare(a.LoudnessLufs, b.LoudnessLufs),
        TrackColumns.Dark        => a.DarkScore.CompareTo(b.DarkScore),
        TrackColumns.Hypnotic    => a.HypnoticScore.CompareTo(b.HypnoticScore),
        TrackColumns.Groove      => a.GrooveScore.CompareTo(b.GrooveScore),
        TrackColumns.Punch       => a.PunchScore.CompareTo(b.PunchScore),
        TrackColumns.Harsh       => a.HarshScore.CompareTo(b.HarshScore),
        TrackColumns.Comment     => Text(a.CommentText, b.CommentText),
        TrackColumns.Cover       => a.HasCover.CompareTo(b.HasCover),
        // None < Outdated < Current, so ascending puts the work first: what has never been
        // analysed, then what is a version behind, then what is done.
        TrackColumns.Analysis    => a.Freshness.CompareTo(b.Freshness),
        TrackColumns.Id3         => Text(a.Id3Text, b.Id3Text),
        TrackColumns.FileType    => Text(a.FileTypeText, b.FileTypeText),
        TrackColumns.FileName    => Text(a.FileNameText, b.FileNameText),
        TrackColumns.Duration    => Nullable.Compare(a.Model.Metadata?.Duration, b.Model.Metadata?.Duration),
        TrackColumns.Like        => a.IsFavorite.CompareTo(b.IsFavorite),
        _                        => 0,
    };

    private static int Text(string? a, string? b) => NaturalComparer.Instance.Compare(a, b);
}
