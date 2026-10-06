using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Metadata;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// One file of an ANALYZE run: measure it, read what it carries now, write what we measured.
///
/// <para>Its own class so the rules below are pinned by a test - the view model that drives the
/// batch needs a window's worth of services to exist, this needs four.</para>
///
/// <para>A failure THROWS, always. The batch runner counts an exception as a failed file and names
/// it in the run's report; anything that returned quietly instead would be counted as analysed.</para>
/// </summary>
public sealed class AnalysisFileWrite
{
    /// <summary>The reason a file whose tags could not be read is left alone - what the run's report
    /// and the log say next to its name.</summary>
    public const string UnreadableTags =
        "its tags could not be read (locked or unreachable), so nothing was written";

    private readonly ITrackAnalysisService _analysis;
    private readonly IMetadataReader _reader;
    private readonly IMetadataWriter _writer;
    private readonly TagWriteGate _writes;

    public AnalysisFileWrite(ITrackAnalysisService analysis, IMetadataReader reader,
                             IMetadataWriter writer, TagWriteGate writes)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
    }

    public async Task RunAsync(string path, CancellationToken ct)
    {
        var detected = await _analysis.AnalyzeAsync(path, null, ct).ConfigureAwait(false);

        // The DSP ran HERE - the one honest timestamp for this measurement.
        var analysedAt = DateTime.UtcNow;

        // (!) NO cancellation check between the decode and the write. Cancellation stops new files
        // from STARTING and aborts a decode in flight; a file whose DSP already finished is written,
        // because the expensive part is spent and throwing the result away would mean STOP quietly
        // undid work that was done. "Whatever finished stays finished" is the promise the STOP
        // button makes, and this is where it is kept.

        // (!) Read the FILE, not the row. A row hydrated from the library index has tags but no
        // blob (TrackIndexMapping.ToMetadata stores no StoredAnalysis), and the blob is what carries
        // the per-field KEPT decisions. Deciding the write off the row would therefore overwrite a
        // key the user had hand-corrected, on exactly the folders the index covers. One extra file
        // open against a whole decode is nothing.
        var current = _reader.Read(path);

        // (!!) A read that FAILED is not a file without tags. The record is then only the bare
        // fallback - no blob, so no KEPT decisions and no stashed original value - and writing on
        // top of it would overwrite a hand-corrected key and lose the original for good, over a
        // share that hiccuped for a second. The file is skipped, counted as failed and named; the
        // next run picks it up.
        if (current.ReadFailed) throw new IOException(UnreadableTags);

        // The suite's ONE composition of "what our detected values put in a file" - blob + standard
        // tags + ReplayGain + the decision stamps, and it leaves a KEPT field alone.
        var write = AnalysisTagWrite.ForDetected(detected, current, analysedAt);
        if (write is null) return;

        // The SAME gate the editor saves through: it releases the file if the preview is holding
        // it, then writes. Write, not Execute: a failure must throw here (see the summary).
        _writes.Write(path, p => _writer.Write(p, write));
    }
}
