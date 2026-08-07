namespace JustPlay.Library;

/// <summary>
/// A filter over the library index - the set-building surface. Every field is optional; the ones
/// that are set are ANDed together.
///
/// <para>This is what makes 0.6 worth a database: "everything between 126 and 130 BPM, in 8A or a
/// neighbouring key, energy 7+, grid-solid, not harsh" has to stay instant whether the library
/// holds 7 thousand tracks or 7 million.</para>
/// </summary>
public sealed record LibraryQuery
{
    /// <summary>Free text - matched against title, artist and album (case-insensitive substring).</summary>
    public string? Text { get; init; }

    /// <summary>
    /// Restrict to tracks under this folder, recursively. This is what lets the finder keep its
    /// Norton-Commander feel while reading the index: the left pane still selects a folder, and
    /// LIBRARY scope means "everything below it" instead of "the files directly in it".
    /// </summary>
    public string? PathPrefix { get; init; }

    /// <summary>
    /// Whether <see cref="PathPrefix"/> reaches into sub-folders. False = that folder's own tracks
    /// only, which is what the finder asks for when it paints a single directory out of the index.
    /// </summary>
    public bool Recursive { get; init; } = true;

    public double? BpmMin { get; init; }
    public double? BpmMax { get; init; }

    /// <summary>Camelot codes to accept, e.g. ["8A", "7A", "9A", "8B"]. Empty/null = any key.</summary>
    public IReadOnlyCollection<string>? Camelot { get; init; }

    public int? EnergyMin { get; init; }
    public int? EnergyMax { get; init; }

    /// <summary>Beat taxonomy label (4x4-driving, groovy, offbeat-bass, breaks, halftime).</summary>
    public string? BeatType { get; init; }

    /// <summary>Minimum beat-grid confidence. (!) 0.45 = grid-soft, 0.25 = grid-fail.</summary>
    public double? MinGridConfidence { get; init; }

    /// <summary>Maximum harshness - the anti-fatigue filter.</summary>
    public double? MaxHarshness { get; init; }

    /// <summary>Include tracks the last sync could not find on disk. Default false.</summary>
    public bool IncludeMissing { get; init; }

    /// <summary>Only tracks whose analysis succeeded. Default true.</summary>
    public bool SuccessOnly { get; init; } = true;

    /// <summary>Hard row cap (0 = no limit). Guards a UI that asked for the whole library.</summary>
    public int Limit { get; init; }

    /// <summary>Sort column. Null = by path.</summary>
    public LibrarySort Sort { get; init; } = LibrarySort.Path;

    public bool Descending { get; init; }
}

/// <summary>Sortable columns - mirrors the finder's toggleable columns.</summary>
public enum LibrarySort
{
    Path,
    Artist,
    Title,
    Bpm,
    Key,
    Energy,
    Duration,
    AnalysedAt,
}
