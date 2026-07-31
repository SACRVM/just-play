namespace JustPlay.Core.Models;

/// <summary>
/// What would happen to one tag field if a candidate <see cref="TagWrite"/> were actually
/// applied, WITHOUT touching the file — produced by <see cref="Abstractions.ITagWritePreview"/>.
/// </summary>
public enum TagWriteAction
{
    /// <summary>The field is currently empty/absent — the candidate value would be written fresh.</summary>
    Write,

    /// <summary>
    /// A different, non-empty value already occupies the field — writing would replace it. This
    /// is the case Chloe specifically wants surfaced ("...und machen drauf aufmerksam welche tags
    /// wir schreiben und ggf überschreiben").
    /// </summary>
    Overwrite,

    /// <summary>The current value already matches the candidate value — writing would be a no-op.</summary>
    Unchanged,

    /// <summary>
    /// The candidate specifies a value for this field, but <see cref="TagWritePolicy"/> does not
    /// allow this field's <see cref="TagFrameFamily"/> — the field would be left exactly as-is,
    /// regardless of whether it would otherwise have been a Write or an Overwrite.
    /// </summary>
    SkippedByPolicy,
}

/// <summary>
/// The planned outcome for one concrete tag field. Most families map to exactly one field
/// (Bpm, Key, Energy, Comment, Grouping, Rating, JustPlayBlob); <see cref="TagFrameFamily.ReplayGain"/>
/// produces two rows (track gain + peak) sharing one policy toggle, since both are always sourced
/// from the same loudness measurement.
/// </summary>
public sealed record TagFieldPlan
{
    public required TagFrameFamily Family { get; init; }

    /// <summary>Human-readable field label for a preview UI, e.g. "BPM", "Key", "ReplayGain (peak)".</summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// The value currently in the file, formatted for display. Null means the field is empty/absent
    /// — matches <see cref="TagWriteAction.Write"/> unless overridden by policy.
    /// </summary>
    public string? CurrentValue { get; init; }

    /// <summary>The value that would be written, formatted for display.</summary>
    public required string ProposedValue { get; init; }

    public required TagWriteAction Action { get; init; }
}

/// <summary>
/// The full dry-run result for one file: one <see cref="TagFieldPlan"/> row per field that the
/// candidate <see cref="TagWrite"/> actually specified (a null field in <see cref="TagWrite"/>
/// means "nothing to write" and never produces a row, regardless of policy). Strictly
/// descriptive — see <see cref="Abstractions.ITagWritePreview"/> for the read-only guarantee.
/// </summary>
public sealed record TagWritePlan
{
    public required string FilePath { get; init; }
    public required IReadOnlyList<TagFieldPlan> Fields { get; init; }

    /// <summary>
    /// True if applying the candidate write would actually change at least one field (i.e. any
    /// field's action is <see cref="TagWriteAction.Write"/> or <see cref="TagWriteAction.Overwrite"/>).
    /// </summary>
    public bool HasChanges => Fields.Any(f => f.Action is TagWriteAction.Write or TagWriteAction.Overwrite);

    /// <summary>
    /// True if applying the candidate write would replace at least one existing, different value —
    /// the case Chloe wants surfaced prominently in the (future) UI.
    /// </summary>
    public bool HasOverwrites => Fields.Any(f => f.Action == TagWriteAction.Overwrite);
}
