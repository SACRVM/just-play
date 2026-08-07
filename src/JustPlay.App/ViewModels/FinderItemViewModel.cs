using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JustPlay.Core.Models;
using JustPlay.UI.ViewModels;

namespace JustPlay.App.ViewModels;

/// <summary>
/// One FILE row in the PRE CUE FINDER listing (folders live in the separate directory tree
/// since P1.1 - hard separation, DJ-software style). Wraps a <see cref="TrackViewModel"/>
/// (metadata arrives lazily in the background, same as queue rows) so Key/BPM/NRG cells and
/// the detail panel reuse the exact display logic the queue already has.
/// </summary>
public sealed partial class FinderItemViewModel : ObservableObject
{
    public FinderItemViewModel(string fullPath)
    {
        FullPath = fullPath;
        NormalizedPath = PreCueFinderViewModel.NormalizePath(fullPath);
        Track = new TrackViewModel(new Track(fullPath));
        CodecLabel = CodecLabelFor(fullPath);
        IsLossless = IsLosslessExt(Path.GetExtension(fullPath));
    }

    public string FullPath { get; }

    /// <summary>Load-order index (natural / playlist order). Restores the "default" (unsorted) order
    /// when column sorting is toggled off - mirrors <see cref="TrackViewModel.AddOrder"/>.</summary>
    public int Order { get; set; }

    /// <summary>Short codec badge shown in the left gutter (FLAC/MP3/OGG/WAV...), so the row never has
    /// a big empty status column - it's swapped for > while auditioning (Chloe 2026-07-06).</summary>
    public string CodecLabel { get; }

    /// <summary>Lossless container (FLAC/WAV/AIFF) - tints the codec badge apart from lossy formats.</summary>
    public bool IsLossless { get; }

    /// <summary>Codec badge shows only when this row isn't auditioning (>) or already queued (ok) - the
    /// 28-px cell carries exactly one glyph, priority > &gt; ok &gt; codec.</summary>
    public bool ShowCodec => !IsCued && !IsOnPlaylist;

    /// <summary>ext -> uppercase badge; the few that aren't just the extension are mapped by hand.</summary>
    private static string CodecLabelFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "MP3",
        ".flac" => "FLAC",
        ".wav" => "WAV",
        ".m4a" => "M4A",
        ".aac" => "AAC",
        ".ogg" => "OGG",
        ".opus" => "OPUS",
        ".wma" => "WMA",
        ".aiff" or ".aif" => "AIFF",
        var e => e.TrimStart('.').ToUpperInvariant(),
    };

    private static bool IsLosslessExt(string ext) =>
        ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".aif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cached normalised path for playlist-membership matching (see
    /// <see cref="PreCueFinderViewModel.NormalizePath"/>).</summary>
    public string NormalizedPath { get; }

    public TrackViewModel Track { get; }

    /// <summary>
    /// The playlists this track appears in, when the pane is showing a folder's playlists together
    /// (Chloe 2026-07-31: <i>"wenn die lib aktiv ist koennten wir doch auch ueber mehrere playlisten
    /// hinweg filtern"</i>). A song that sits in five sets is ONE row - you are looking for the
    /// track, not for its occurrences - so where it came from has to live on the row instead.
    /// Empty for a plain folder listing.
    /// </summary>
    public IReadOnlyList<string> InPlaylists { get; set; } = [];

    /// <summary>"Warehouse, Peak Time, Closing" - the detail pane's provenance line. Null when this
    /// row did not come from a playlist.</summary>
    public string? InPlaylistsText =>
        InPlaylists.Count == 0 ? null : string.Join(", ", InPlaylists);

    // One-shot hydration claim. Metadata is fetched-what-you-see: the visible viewport and a parallel
    // background fill RACE to hydrate each row; this CAS lets exactly ONE of them win, so a file is read
    // once no matter who reaches it first. 0 = free, 1 = claimed. See PreCueFinderViewModel.HydrateItem.
    private int _claimed;

    /// <summary>Atomically claim this row for the ONE caller that will read its metadata; everyone else
    /// (viewport or background pool) gets false and must not read.</summary>
    public bool TryClaimHydration() => System.Threading.Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    /// <summary>ok - this file is on the CURRENT queue (live: appears the moment + adds it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQueuedTick))]
    [NotifyPropertyChangedFor(nameof(ShowCodec))]
    private bool _isOnPlaylist;

    /// <summary>This row is the one currently auditioning on the cue device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQueuedTick))]
    [NotifyPropertyChangedFor(nameof(ShowCodec))]
    [NotifyPropertyChangedFor(nameof(ShowCuePlaying))]
    [NotifyPropertyChangedFor(nameof(ShowCuePaused))]
    private bool _isCued;

    /// <summary>This (cued) row is paused (Space) rather than sounding - kept in sync by the VM so the
    /// status glyph flips > <-> pause (Chloe 2026-07-06).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCuePlaying))]
    [NotifyPropertyChangedFor(nameof(ShowCuePaused))]
    private bool _paused;

    /// <summary>> - this row is cued AND sounding.</summary>
    public bool ShowCuePlaying => IsCued && !Paused;

    /// <summary>pause - this row is cued but paused.</summary>
    public bool ShowCuePaused => IsCued && Paused;

    /// <summary>The status cell shows ONE glyph: the cue transport (> / pause) takes priority, otherwise the
    /// ok queue tick - so they never overlap in the gutter.</summary>
    public bool ShowQueuedTick => IsOnPlaylist && !IsCued;
}
