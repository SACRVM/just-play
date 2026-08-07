using System.Globalization;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>
/// Read-only dry-run companion to <see cref="TagLibMetadataWriter"/>: reads a file's CURRENT tag
/// values with TagLib# and compares them against a candidate <see cref="TagWrite"/>, without ever
/// calling <c>file.Save()</c> - see <see cref="ITagWritePreview"/>'s read-only guarantee.
///
/// <para>
/// Deliberately mirrors, field-for-field, exactly what <c>TagLibMetadataWriter.WriteCore</c>
/// writes (same target frames/fields, same formatting) so this preview can never quietly drift
/// from the writer it is previewing:
/// </para>
/// <list type="bullet">
///   <item>BPM - <c>Tag.BeatsPerMinute</c>, rounded/clamped exactly like <c>SetBpm</c>.</item>
///   <item>Key - <c>Tag.InitialKey</c>, parsed with <see cref="MusicalKey.TryParse"/> so a
///     musical-notation value ("Gm") written by another tool compares equal to the Camelot code
///     ("6A") we would write for the same key - mirrors how <c>PromoteCommand</c>/Persist already
///     compare "the original tag value" elsewhere in this codebase.</item>
///   <item>Energy / JustPlay blob / ReplayGain - the same <see cref="TagCustomFields"/> keys
///     ("ENERGY" / "JUSTPLAY" / "REPLAYGAIN_TRACK_GAIN" / "REPLAYGAIN_TRACK_PEAK").</item>
///   <item>Comment / Grouping - <c>Tag.Comment</c> / <c>Tag.Grouping</c>, the same properties
///     <c>ApplyCleanComment</c> and the plain Grouping setter target.</item>
///   <item>Rating - <see cref="TagLibMetadataReader.ReadPopm"/> (shared, not duplicated).</item>
/// </list>
/// </summary>
public sealed class TagLibWritePreview : ITagWritePreview
{
    public TagWritePlan Preview(string filePath, TagWrite candidate, TagWritePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);

        // Read-only use of TagLib#: this handle is never Save()d, so nothing it touches is
        // ever persisted back to disk (see the interface's read-only guarantee).
        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        var fields = new List<TagFieldPlan>();

        if (candidate.Bpm is { } bpm)
            fields.Add(PlanBpm(tag, bpm, policy.AllowBpm));

        if (candidate.Key is { } key)
            fields.Add(PlanKey(tag, key, policy.AllowKey));

        if (candidate.Energy is { } energy)
            fields.Add(PlanEnergy(file, energy, policy.AllowEnergy));

        if (candidate.State is { } state)
            fields.Add(PlanJustPlayBlob(file, state, policy.AllowJustPlayBlob));

        if (candidate.Comment is { } comment)
            fields.Add(PlanTextField(TagFrameFamily.Comment, "Comment", tag.Comment, comment, policy.AllowComment));

        if (candidate.Grouping is { } grouping)
            fields.Add(PlanTextField(TagFrameFamily.Grouping, "Grouping", tag.Grouping, grouping, policy.AllowGrouping));

        if (candidate.Favorite is { } favorite)
            fields.Add(PlanRating(file, favorite, policy.AllowRating));

        if (candidate.ReplayGainDb is { } gainDb)
            fields.Add(PlanReplayGainField(file, "REPLAYGAIN_TRACK_GAIN", "ReplayGain (track gain)",
                gainDb.ToString("0.00", CultureInfo.InvariantCulture) + " dB", policy.AllowReplayGain));

        if (candidate.Peak is { } peak)
            fields.Add(PlanReplayGainField(file, "REPLAYGAIN_TRACK_PEAK", "ReplayGain (peak)",
                peak.ToString("0.000000", CultureInfo.InvariantCulture), policy.AllowReplayGain));

        return new TagWritePlan { FilePath = filePath, Fields = fields };
    }

    // -------------------------------------------------------------------------
    // Per-field planners - one per TagWrite member, mirroring TagLibMetadataWriter 1:1.
    // -------------------------------------------------------------------------

    /// <summary>Mirrors <c>TagLibMetadataWriter.SetBpm</c> (TagLibMetadataWriter.cs:275-281).</summary>
    private static TagFieldPlan PlanBpm(TagLib.Tag tag, double bpm, bool allowed)
    {
        var proposed = (uint)Math.Clamp(Math.Round(bpm), 0, 999);
        var current = tag.BeatsPerMinute; // 0 = unset, same convention TagLibMetadataReader uses.
        var currentIsEmpty = current == 0;
        var matches = !currentIsEmpty && current == proposed;

        return Plan(TagFrameFamily.Bpm, "BPM",
            currentIsEmpty ? null : current.ToString(CultureInfo.InvariantCulture),
            proposed.ToString(CultureInfo.InvariantCulture),
            currentIsEmpty, matches, allowed);
    }

    /// <summary>Mirrors <c>TagLibMetadataWriter.SetContractKey</c> (TagLibMetadataWriter.cs:259-268).
    /// Compares on the PARSED Camelot value (not raw text) so "Gm" and "6A" - the same key in two
    /// notations - read as Unchanged, exactly like the rest of the codebase (e.g. PromoteCommand)
    /// already treats "the current key".</summary>
    private static TagFieldPlan PlanKey(TagLib.Tag tag, MusicalKey key, bool allowed)
    {
        var proposed = key.Camelot;
        var currentRaw = string.IsNullOrWhiteSpace(tag.InitialKey) ? null : tag.InitialKey!.Trim();
        var currentIsEmpty = currentRaw is null;
        var currentParsed = currentRaw is null ? null : MusicalKey.TryParse(currentRaw);
        var matches = currentParsed is { } cp && string.Equals(cp.Camelot, proposed, StringComparison.OrdinalIgnoreCase);

        return Plan(TagFrameFamily.Key, "Key", currentRaw, proposed, currentIsEmpty, matches, allowed);
    }

    /// <summary>Mirrors the ENERGY custom-field write (TagLibMetadataWriter.cs:43-44).</summary>
    private static TagFieldPlan PlanEnergy(TagLib.File file, int energy, bool allowed)
    {
        var currentRaw = TagCustomFields.Get(file, "ENERGY");
        var currentIsEmpty = string.IsNullOrWhiteSpace(currentRaw);
        var currentParsed = int.TryParse(currentRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)
            ? e : (int?)null;
        var matches = !currentIsEmpty && currentParsed == energy;

        return Plan(TagFrameFamily.Energy, "Energy", currentRaw,
            energy.ToString(CultureInfo.InvariantCulture), currentIsEmpty, matches, allowed);
    }

    /// <summary>Mirrors the JUSTPLAY blob write (TagLibMetadataWriter.cs:46-47). Compared byte-for-byte
    /// on the serialized JSON (any drift in a single detected value is a real, meaningful change),
    /// but DISPLAYED as a short version summary rather than the raw blob.</summary>
    private static TagFieldPlan PlanJustPlayBlob(TagLib.File file, TrackAnalysisState state, bool allowed)
    {
        var proposedRaw = AnalysisStateCodec.Serialize(state);
        var currentRaw = TagCustomFields.Get(file, "JUSTPLAY");
        var currentIsEmpty = string.IsNullOrEmpty(currentRaw);
        var matches = !currentIsEmpty && string.Equals(currentRaw, proposedRaw, StringComparison.Ordinal);

        string? currentDisplay = null;
        if (!currentIsEmpty)
        {
            var parsed = AnalysisStateCodec.TryParse(currentRaw);
            currentDisplay = parsed is not null ? $"present (v{parsed.Version})" : "present (unparseable)";
        }

        return Plan(TagFrameFamily.JustPlayBlob, "JustPlay analysis blob",
            currentDisplay, $"v{state.Version}", currentIsEmpty, matches, allowed);
    }

    /// <summary>
    /// Mirrors <c>ApplyCleanComment</c> (Comment, TagLibMetadataWriter.cs:302-333) and the plain
    /// Grouping setter (TagLibMetadataWriter.cs:52-53) - both are simple string tag properties
    /// where "" is a legitimate explicit "clear" request, so equality (not raw non-null-ness)
    /// decides Unchanged vs. Write/Overwrite.
    /// </summary>
    private static TagFieldPlan PlanTextField(TagFrameFamily family, string fieldName,
        string? currentRaw, string proposedRaw, bool allowed)
    {
        var currentIsEmpty = string.IsNullOrEmpty(currentRaw);
        var matches = string.Equals(currentRaw ?? "", proposedRaw, StringComparison.Ordinal);
        var proposedDisplay = proposedRaw.Length == 0 ? "(cleared)" : proposedRaw;

        return Plan(family, fieldName, currentRaw, proposedDisplay, currentIsEmpty, matches, allowed);
    }

    /// <summary>Mirrors <c>SetPopm</c> (TagLibMetadataWriter.cs:341-369), reading via the SAME
    /// <see cref="TagLibMetadataReader.ReadPopm"/> the app's "is this liked" logic uses.</summary>
    private static TagFieldPlan PlanRating(TagLib.File file, bool favorite, bool allowed)
    {
        var currentLiked = TagLibMetadataReader.ReadPopm(file);
        var matches = currentLiked == favorite;

        return Plan(TagFrameFamily.Rating, "Rating",
            currentLiked ? "Liked" : null, favorite ? "Liked" : "Not liked",
            !currentLiked, matches, allowed);
    }

    /// <summary>Mirrors the REPLAYGAIN_TRACK_GAIN / _PEAK custom-field writes (TagLibMetadataWriter.cs:58-64).
    /// Called once per field (gain, peak) - both share the <see cref="TagFrameFamily.ReplayGain"/> family.</summary>
    private static TagFieldPlan PlanReplayGainField(TagLib.File file, string tagKey, string fieldName,
        string proposedRaw, bool allowed)
    {
        var currentRaw = TagCustomFields.Get(file, tagKey);
        var currentIsEmpty = string.IsNullOrEmpty(currentRaw);
        var matches = string.Equals(currentRaw ?? "", proposedRaw, StringComparison.Ordinal);

        return Plan(TagFrameFamily.ReplayGain, fieldName, currentRaw, proposedRaw, currentIsEmpty, matches, allowed);
    }

    // -------------------------------------------------------------------------
    // Shared action decision - the one place Write/Overwrite/Unchanged/SkippedByPolicy is decided.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The single decision point for every field: policy wins first (a disallowed family is
    /// always SkippedByPolicy, even if the value wouldn't actually change); otherwise a match
    /// against the current value is Unchanged; otherwise an empty current field is a fresh Write
    /// and a non-empty one is an Overwrite.
    /// </summary>
    private static TagFieldPlan Plan(TagFrameFamily family, string fieldName,
        string? currentDisplay, string proposedDisplay, bool currentIsEmpty, bool matches, bool allowed)
    {
        var action = !allowed ? TagWriteAction.SkippedByPolicy
            : matches ? TagWriteAction.Unchanged
            : currentIsEmpty ? TagWriteAction.Write
            : TagWriteAction.Overwrite;

        return new TagFieldPlan
        {
            Family = family,
            FieldName = fieldName,
            CurrentValue = currentIsEmpty ? null : currentDisplay,
            ProposedValue = proposedDisplay,
            Action = action,
        };
    }
}
