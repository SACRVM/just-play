namespace JustPlay.Core.Models;

/// <summary>Which physical tag container a <see cref="RawTagContainer"/> came from.</summary>
public enum RawTagContainerKind
{
    Id3v2,
    Id3v1,
    Xiph,
    Ape,
    Mp4,
}

/// <summary>
/// One tag container's worth of raw entries (a file can carry more than one container - e.g. an
/// MP3 with both an ID3v2 tag and a legacy ID3v1 trailer). <see cref="UnsupportedReason"/> is set
/// instead of <see cref="Entries"/> being populated when the reader does not (yet) walk that
/// container type in detail - a documented gap, never a silently empty table.
/// </summary>
public sealed record RawTagContainer
{
    public required RawTagContainerKind Kind { get; init; }

    /// <summary>Display label, e.g. "ID3v2.3", "Xiph (Vorbis comments)", "APEv2".</summary>
    public required string Label { get; init; }

    public required IReadOnlyList<RawTagEntry> Entries { get; init; }

    /// <summary>
    /// Non-null when this container exists on disk but the reader cannot enumerate it in detail
    /// yet; <see cref="Entries"/> is empty in that case.
    /// </summary>
    public string? UnsupportedReason { get; init; }
}

/// <summary>
/// The full raw-tag read for one file, produced by <see cref="Abstractions.IRawTagReader"/>. A file
/// that could not be opened or parsed at all comes back with <see cref="Containers"/> empty and
/// <see cref="FailureReason"/> set - never an exception.
/// </summary>
public sealed record RawTagReadResult
{
    public required string FilePath { get; init; }
    public required IReadOnlyList<RawTagContainer> Containers { get; init; }

    /// <summary>Non-null when the file could not be read at all.</summary>
    public string? FailureReason { get; init; }

    public bool Success => FailureReason is null;

    /// <summary>Total entries across every container - the count a "raw tags" window would show
    /// in its summary line.</summary>
    public int TotalEntryCount => Containers.Sum(c => c.Entries.Count);

    /// <summary>Every entry whose owner the reader could name. Kept as a projection over the
    /// containers rather than a second stored list, so it cannot drift from them.
    /// <para>Nothing in the UI aggregates it any more - ownership is stated per row now, not summed
    /// up above the table (see <see cref="Abstractions.IRawTagReader"/>). It stays because "which
    /// entries did we attribute at all" is the question this reader is judged on, and the corpus
    /// test that answers it asks it through here.</para></summary>
    public IEnumerable<RawTagEntry> VendorEntries =>
        Containers.SelectMany(c => c.Entries).Where(e => e.Vendor != RawTagVendor.Unknown);
}
