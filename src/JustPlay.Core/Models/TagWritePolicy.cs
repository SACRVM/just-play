namespace JustPlay.Core.Models;

/// <summary>
/// The tag "frame families" JustPlay's analysis-driven write path can touch — i.e. exactly the
/// non-null fields of <see cref="TagWrite"/> (and its inverse, <see cref="TagRestore"/>), as
/// written by <see cref="Abstractions.IMetadataWriter.Write"/>. One family per line below,
/// verified against <c>TagLibMetadataWriter.WriteCore</c>:
///
/// <list type="bullet">
///   <item><see cref="JustPlayBlob"/> — <c>TagWrite.State</c>, TagLibMetadataWriter.cs:46-47.</item>
///   <item><see cref="Bpm"/> — <c>TagWrite.Bpm</c>, TagLibMetadataWriter.cs:37-38 / SetBpm:275-281.</item>
///   <item><see cref="Key"/> — <c>TagWrite.Key</c>, TagLibMetadataWriter.cs:40-41 / SetContractKey:259-268.</item>
///   <item><see cref="Energy"/> — <c>TagWrite.Energy</c>, TagLibMetadataWriter.cs:43-44.</item>
///   <item><see cref="Comment"/> — <c>TagWrite.Comment</c>, TagLibMetadataWriter.cs:49-50 / ApplyCleanComment:302-333.</item>
///   <item><see cref="Grouping"/> — <c>TagWrite.Grouping</c>, TagLibMetadataWriter.cs:52-53.</item>
///   <item><see cref="Rating"/> — <c>TagWrite.Favorite</c>, TagLibMetadataWriter.cs:55-56 / SetPopm:341-369.</item>
///   <item><see cref="ReplayGain"/> — <c>TagWrite.ReplayGainDb</c> + <c>TagWrite.Peak</c>,
///     TagLibMetadataWriter.cs:58-64 (always sourced from the same loudness measurement, so one
///     family covers both fields).</item>
/// </list>
///
/// <para>
/// ⚠ <b>Deliberately out of scope:</b> the always-explicit editorial fields (title, artist, album,
/// album artist, genre, year, track number, cover art / APIC) written by
/// <see cref="Abstractions.IMetadataWriter.WriteEditable"/> via <see cref="EditableTags"/> +
/// <see cref="CoverAction"/> are NOT a family here. That path is a manual, single-file edit a
/// person typed in JUST TAG's editor — there is no "would this silently overwrite something you
/// didn't expect" question to answer, unlike the automatic analysis write this policy gates.
/// </para>
///
/// <para>
/// ⚠ <b>Also verified absent:</b> JustPlay never writes a Serato GEOB frame. The milestone doc's
/// family wishlist named it as an example, but the writer only ever PRESERVES existing GEOB /
/// Serato-Markers attachments during a cover-art operation (see
/// <c>TagLibMetadataWriter.KeepNonPictureAttachments</c>) — it never creates or modifies one. There
/// is nothing to gate because there is no write path to gate.
/// </para>
///
/// <para>
/// ⚠ <b>Naming correction:</b> JustPlay's own energy custom field is <c>TXXX:ENERGY</c>
/// (<c>TagCustomFields</c> key "ENERGY"), not Mixed In Key's <c>TXXX:EnergyLevel</c> — that MIK
/// field is one JustPlay only ever READS (see the dj-metadata-interop skill), never writes.
/// </para>
/// </summary>
public enum TagFrameFamily
{
    /// <summary>The JUSTPLAY analysis-state blob (ID3v2 TXXX:JUSTPLAY / Xiph JUSTPLAY).</summary>
    JustPlayBlob,

    /// <summary>Standard tempo tag (ID3v2 TBPM via <c>Tag.BeatsPerMinute</c>; Xiph BPM on FLAC).</summary>
    Bpm,

    /// <summary>
    /// Standard key tag, written as the Camelot code (ID3v2 TKEY via <c>Tag.InitialKey</c>;
    /// Xiph INITIALKEY/KEY/TKEY on FLAC).
    /// </summary>
    Key,

    /// <summary>The non-standard ENERGY custom field (ID3v2 TXXX:ENERGY / Xiph ENERGY), 1..10.</summary>
    Energy,

    /// <summary>
    /// The single clean "DJ Software compatible" comment segment, e.g. <c>8A - Energy 7</c>
    /// (ID3v2 one COMM frame, lang="eng" desc=""; Xiph COMMENT+DESCRIPTION).
    /// </summary>
    Comment,

    /// <summary>
    /// Content Group / Grouping tag (ID3v2 TIT1 / Xiph GROUPING / MP4 ©grp). Written today only
    /// by the CLI's batch tagger (<c>tag write</c> / <c>tag clean</c>) — the app's own Persist
    /// path never sets it.
    /// </summary>
    Grouping,

    /// <summary>The favourite/"loved" flag (ID3v2 POPM rating 255; Xiph RATING "5").</summary>
    Rating,

    /// <summary>
    /// <c>REPLAYGAIN_TRACK_GAIN</c> + <c>REPLAYGAIN_TRACK_PEAK</c> together — always written (or
    /// left alone) as a pair, since both come from the same loudness measurement.
    /// </summary>
    ReplayGain,
}

/// <summary>
/// Shared, platform-agnostic, serializable policy describing which <see cref="TagFrameFamily"/>
/// families JUST PLAY, JUST TAG, and the CLI are allowed to write. ONE implementation for the
/// whole suite (repo memory: suite-unify-dont-patch-divergence) — do not fork a second copy per
/// app; add fields here and share this type.
///
/// <para>
/// ⚠ <b>Invariant — a user who never opens the (future) policy screen must see ZERO behaviour
/// change.</b> Every family below defaults to allowed, because today's writer has no gate at all:
/// every non-null <see cref="TagWrite"/> field is unconditionally written. "No policy" and
/// "policy = allow everything" must therefore be indistinguishable. Pinned by
/// <c>TagWritePolicyTests.Defaults_AllowEveryFamily_MatchingTodaysUngatedWriter</c>.
/// </para>
///
/// <para>
/// This object is descriptive only in this half of the milestone: nothing in this change wires it
/// into <see cref="Abstractions.IMetadataWriter"/>, settings persistence, DI, or any UI. See the
/// seam documented on <see cref="Abstractions.ITagWritePreview"/> for where enforcement would hook
/// in when that (daytime) work happens.
/// </para>
/// </summary>
public sealed record TagWritePolicy
{
    public bool AllowJustPlayBlob { get; init; } = true;
    public bool AllowBpm { get; init; } = true;
    public bool AllowKey { get; init; } = true;
    public bool AllowEnergy { get; init; } = true;
    public bool AllowComment { get; init; } = true;
    public bool AllowGrouping { get; init; } = true;
    public bool AllowRating { get; init; } = true;
    public bool AllowReplayGain { get; init; } = true;

    /// <summary>The default policy — every family allowed, reproducing today's ungated behaviour exactly.</summary>
    public static readonly TagWritePolicy AllowAll = new();

    /// <summary>Whether <paramref name="family"/> is currently allowed to be written under this policy.</summary>
    public bool Allows(TagFrameFamily family) => family switch
    {
        TagFrameFamily.JustPlayBlob => AllowJustPlayBlob,
        TagFrameFamily.Bpm          => AllowBpm,
        TagFrameFamily.Key          => AllowKey,
        TagFrameFamily.Energy       => AllowEnergy,
        TagFrameFamily.Comment      => AllowComment,
        TagFrameFamily.Grouping     => AllowGrouping,
        TagFrameFamily.Rating       => AllowRating,
        TagFrameFamily.ReplayGain   => AllowReplayGain,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown TagFrameFamily — add a case above."),
    };
}
