namespace JustPlay.Core.Models;

/// <summary>
/// Settings for the PRE CUE FINDER window. Deliberately a SEPARATE record (and a
/// separate file on disk, <c>finder.settings.json</c>) from <see cref="UserSettings"/> —
/// the finder is an add-on and must not water down the main settings. The one shared
/// value, the cue headphone device, intentionally stays in
/// <see cref="UserSettings.HeadphoneDeviceName"/> so the finder and the pre-cue tab can
/// never disagree about where cue audio goes.
/// </summary>
public sealed record FinderSettings
{
    /// <summary>
    /// Root folder the finder browses (e.g. <c>\\nas\music\GENRES</c>).
    /// Null until the user picks one — the finder then shows its setup hint
    /// instead of a listing.
    /// </summary>
    public string? LibraryRoot { get; init; }

    /// <summary>Seek jump for ←/→ in seconds. One of <see cref="AllowedSeekSteps"/>.</summary>
    public int SeekStepSeconds { get; init; } = 30;

    /// <summary>The only steps the UI offers (60 was explicitly rejected as too coarse).</summary>
    public static readonly int[] AllowedSeekSteps = [15, 20, 30];

    /// <summary>
    /// When true, moving the selection (arrow / click) auto-plays the song on the cue device
    /// after the ~1 s debounce; when false (the DEFAULT), browsing is silent and Enter plays the
    /// selected song. Chloe 2026-07-06: "Return ist der default zum Abspielen … in den Settings
    /// gibt's aber ein auto play song Schalter — damit macht man alle glücklich."
    /// </summary>
    public bool AutoPlayOnSelect { get; init; } = false;

    /// <summary>
    /// The file-list columns the finder shows, mirroring the JUST PLAY queue's toggleable columns
    /// (right-click the header to flip them). Null = <see cref="DefaultColumns"/>. The NAME column is
    /// structural (always shown) and isn't in this set.
    /// </summary>
    public string[]? Columns { get; init; }

    /// <summary>Column ids the finder can show/hide, in display order (JUST PLAY columns + the finder-only
    /// vibe scores dark/hypnotic/groove/punch/harsh, which the finder exposes as the analyzer showcase).</summary>
    public static readonly string[] AllColumns =
        ["artist", "genre", "bpm", "key", "nrg", "gain", "lufs", "dark", "hypnotic", "groove", "punch", "harsh", "comment", "duration", "like"];

    /// <summary>The out-of-the-box visible set: the audition essentials + artist + genre. The finder shows the
    /// artist as its OWN sortable column (unlike the main UI, which stacks it under the title). GAIN/LUFS start
    /// hidden (niche) but are one right-click away.</summary>
    public static readonly string[] DefaultColumns = ["artist", "genre", "bpm", "key", "nrg", "duration", "like"];

    /// <summary>The effective visible-column set (falls back to <see cref="DefaultColumns"/>).</summary>
    public string[] VisibleColumns => Columns ?? DefaultColumns;

    public static readonly FinderSettings Defaults = new();

    /// <summary>
    /// Snaps a hand-edited / legacy <see cref="SeekStepSeconds"/> back onto an allowed
    /// step (default 30) so the segmented radio in the settings flyout always has a
    /// lit segment.
    /// </summary>
    public FinderSettings Normalized() =>
        Array.IndexOf(AllowedSeekSteps, SeekStepSeconds) >= 0
            ? this
            : this with { SeekStepSeconds = 30 };
}
