using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Strictly read-only dry-run companion to <see cref="IMetadataWriter.Write"/>: given a candidate
/// <see cref="TagWrite"/> and a <see cref="TagWritePolicy"/>, reports what WOULD happen to the
/// file's tags - per field, Write / Overwrite / Unchanged / SkippedByPolicy - without writing
/// anything. Implemented in JustPlay.Metadata (TagLib# is only allowed there, per the layering
/// rule in CLAUDE.md); JUST PLAY, JUST TAG and the CLI all reach it through this one interface so
/// the preview logic - like the write logic it mirrors - lives in exactly one place (repo memory:
/// suite-unify-dont-patch-divergence).
///
/// <para>
/// <b>Read-only guarantee:</b> an implementation may open the file to read its current tags but
/// must NEVER call anything that persists a change to it (no <c>TagLib.File.Save()</c>, no direct
/// byte writes). Pinned by <c>TagLibWritePreviewTests.Preview_NeverModifiesFileOnDisk</c>, which
/// hashes the file before/after and asserts the bytes are identical.
/// </para>
///
/// <para>
/// <b>Seam for enforcing the policy in the real writer (not built in this half - see the
/// milestone's P3 scope):</b> <c>TagLibMetadataWriter.WriteCore</c>
/// (<c>src/JustPlay.Metadata/TagLibMetadataWriter.cs</c>) would need each
/// <c>if (write.X is { } x) ...</c> guard changed to also require
/// <c>policy.AllowX</c>, e.g. line 37-38 becomes
/// <c>if (write.Bpm is { } bpm &amp;&amp; policy.AllowBpm) SetBpm(file, tag, bpm);</c> - one extra
/// <c>&amp;&amp;</c> per field, and <see cref="IMetadataWriter.Write"/> would need a
/// <see cref="TagWritePolicy"/> parameter (defaulting to <see cref="TagWritePolicy.AllowAll"/> so
/// every existing caller keeps today's behaviour). Deliberately NOT done here per the "don't change
/// the writer's behaviour" constraint for this change.
/// </para>
/// </summary>
public interface ITagWritePreview
{
    /// <summary>
    /// Build the plan for <paramref name="candidate"/> against the file at <paramref name="filePath"/>,
    /// gated by <paramref name="policy"/>. Only fields <paramref name="candidate"/> actually specifies
    /// (non-null) produce a row - a null field in <see cref="TagWrite"/> means "nothing to write"
    /// regardless of policy, so it is simply absent from the plan.
    /// </summary>
    TagWritePlan Preview(string filePath, TagWrite candidate, TagWritePolicy policy);
}
