namespace JustPlay.Core.Models;

/// <summary>
/// A vendor/tool that owns a recognisable slice of a file's raw tags, when one can be told apart
/// from the id/descriptor string alone. Recognition is a small, explicit, documented table (see
/// <c>JustPlay.Metadata.TagLibRawTagReader</c>'s "Vendor recognition" section) - never a guess -
/// built from the corpus measurement in <c>taglib-byte-preservation.md</c> and the
/// dj-metadata-interop skill.
/// </summary>
public enum RawTagVendor
{
    /// <summary>No known vendor claims this entry - a standard tag, or one nobody has documented yet.</summary>
    Unknown,

    /// <summary>Our own writer: the JUSTPLAY analysis blob, the ENERGY field, the JustPlay POPM rating.</summary>
    JustPlay,

    /// <summary>
    /// Serato DJ - the only major DJ app that embeds cues/loops/beatgrid/waveform IN the file
    /// (GEOB "Serato *" frames / SERATO_* Xiph fields, TXXX:SERATO_PLAYCOUNT).
    /// </summary>
    Serato,

    /// <summary>
    /// Native Instruments Traktor (PRIV owner starting "TRAKTOR", POPM user
    /// "traktor@native-instruments.de"). Traktor's cues/loops/beatgrid live in collection.nml, not
    /// the file - the PRIV blob is its own in-file bookkeeping.
    /// </summary>
    Traktor,

    /// <summary>
    /// Mixed In Key (GEOB "Key" / "Energy" / "CuePoints", TXXX:EnergyLevel). The origin of the
    /// "8A - Energy 7" comment convention.
    /// </summary>
    MixedInKey,

    /// <summary>
    /// Pioneer/AlphaTheta rekordbox. Never actually assigned by the reader: rekordbox keeps no
    /// distinguishing frame in the file at all - its cues/loops/beatgrid/waveform live in ANLZ
    /// files plus master.db, and the only tag it touches is the standard TKEY (confirmed:
    /// dj-metadata-interop skill, db-centric-apps.md Sec.2). Kept as a value so a future UI has a
    /// slot ready, not because this reader can detect rekordbox today.
    /// </summary>
    Rekordbox,
}

/// <summary>
/// One raw tag entry exactly as it sits on disk - a DJ-legible view of a single ID3v2 frame, Xiph
/// comment field, or APE item. Never carries the entry's raw bytes: binary values are summarised
/// (size, and mime type where known), never hex-dumped.
/// </summary>
public sealed record RawTagEntry
{
    /// <summary>
    /// The frame/field identifier: an ID3v2 frame ID ("GEOB", "TXXX", "PRIV", "COMM", "POPM",
    /// "APIC", "TIT2", ...), or - for Xiph/APE, which have no separate id/descriptor split - the
    /// field key itself (e.g. "ARTIST", "SERATO_MARKERS_V2").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The description / owner / key string this entry is looked up BY - what Serato and Mixed In
    /// Key match their frames on. Null when <see cref="Id"/> is already the only handle: Xiph/APE
    /// fields, and ID3v2 frame types that carry no separate descriptor (e.g. standard text frames).
    /// </summary>
    public string? Descriptor { get; init; }

    /// <summary>Size of this entry's payload in bytes, as it was last stored on disk.</summary>
    public required int SizeBytes { get; init; }

    /// <summary>
    /// A short, human-legible value or summary. Text entries show their text (truncated for very
    /// long values); binary entries show a size/mime summary - never their raw bytes.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>The recognised vendor, or <see cref="RawTagVendor.Unknown"/>.</summary>
    public RawTagVendor Vendor { get; init; } = RawTagVendor.Unknown;
}
