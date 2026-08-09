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
/// <b>UI seam (not built by this task):</b> a future <c>RawTagsWindow</c> in <c>JustPlay.UI</c>
/// calls <see cref="Read"/> once per selected file and renders <see cref="RawTagReadResult.Containers"/>
/// as one collapsible section per container ("ID3v2.3", "Xiph", ...), each section a small table of
/// <see cref="RawTagEntry.Id"/> / <see cref="RawTagEntry.Descriptor"/> / <see cref="RawTagEntry.Vendor"/>
/// / <see cref="RawTagEntry.SizeBytes"/> / <see cref="RawTagEntry.Summary"/>. A container with
/// <see cref="RawTagContainer.UnsupportedReason"/> set renders as one greyed-out explanatory row,
/// not an empty table. A non-null <see cref="RawTagReadResult.FailureReason"/> renders as a single
/// "couldn't read this file" line instead of the table. The one thing worth calling out visually in
/// that window is <see cref="RawTagReadResult.VendorEntries"/> - a badge per recognised vendor
/// (Serato / Traktor / Mixed In Key) is the actual "your cues are still here" proof this was built
/// to surface.
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
