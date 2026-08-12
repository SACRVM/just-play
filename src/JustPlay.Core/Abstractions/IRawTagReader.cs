using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Reads a file's raw tag containers - the frames/fields exactly as they sit on disk, not the
/// handful <see cref="IMetadataReader"/> maps onto <see cref="TrackMetadata"/>. Exists to answer
/// one question for a DJ: "is my Serato/Traktor/Mixed In Key data still in this file after JustPlay
/// touched it?" - measured true (787/787 vendor frame payloads preserved byte-identical; see
/// <c>.claude/night-reports/2026-07-31-L3-taglib-bytes.md</c>), but nothing in the suite could show
/// it until this reader existed.
///
/// <para>
/// READ-ONLY BY CONTRACT. An implementation must never call the underlying tag library's
/// persist path (TagLib#'s <c>File.Save()</c>) - see <c>TagLibRawTagReader</c>'s remarks for how
/// that is proven with a before/after SHA-256 test.
/// </para>
///
/// <para>
/// <b>UI seam:</b> the shared tag editor's RAW tab (<c>TagEditorPanel</c> in <c>JustPlay.UI</c>,
/// via <c>TagEditorViewModel.Raw</c>) calls <see cref="Read"/> for the open file and renders
/// <see cref="RawTagReadResult.Containers"/>
/// as one collapsible section per container ("ID3v2.3", "Xiph", ...), each section a small table of
/// <see cref="RawTagEntry.Id"/> / <see cref="RawTagEntry.Descriptor"/> / <see cref="RawTagEntry.Vendor"/>
/// / <see cref="RawTagEntry.SizeBytes"/> / <see cref="RawTagEntry.Summary"/>. A container with
/// <see cref="RawTagContainer.UnsupportedReason"/> set renders as one greyed-out explanatory row,
/// not an empty table. A non-null <see cref="RawTagReadResult.FailureReason"/> renders as a single
/// "couldn't read this file" line instead of the table.
/// </para>
///
/// <para>
/// <see cref="RawTagEntry.Vendor"/> is what makes this more than a frame dump - a row the reader can
/// attribute to Serato / Traktor / Mixed In Key IS the "your cues are still here" proof it was built
/// to surface. It is shown per ROW (in the row's tooltip), never as a summary above the table: a
/// count of somebody's frames is a claim you have to trust, while the row is the thing being looked
/// at. Everything that once aggregated it - a chip strip, then a sentence - was tried and removed.
/// </para>
/// </summary>
public interface IRawTagReader
{
    /// <summary>
    /// Reads every raw tag container present in <paramref name="filePath"/>. Never throws: a file
    /// that cannot be opened or parsed comes back with an empty <see cref="RawTagReadResult.Containers"/>
    /// and a non-null <see cref="RawTagReadResult.FailureReason"/>.
    /// </summary>
    RawTagReadResult Read(string filePath);
}
