using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;

namespace JustPlay.Core.Organise;

/// <summary>One file that did not make it, by name. Never truncated - a dropped name is a dropped
/// track.</summary>
public sealed record OrganiseFailure(string Path, string Message);

/// <summary>
/// Where a run has got to. Bytes rather than files drive the bar, because a folder of tracks is not
/// a folder of equal-sized things and a file counter jumps from 3/4 to 4/4 while the last one is
/// still crawling over SMB.
/// </summary>
public readonly record struct OrganiseProgress(
    int Done,
    int Total,
    long BytesDone,
    long BytesTotal,
    string? CurrentPath,
    bool Verifying)
{
    /// <summary>0..1 for the overlay ring, or null when there is nothing to divide by.</summary>
    public double? Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0d, 1d)
        : Total > 0    ? Math.Clamp((double)Done / Total, 0d, 1d)
        : null;
}

/// <summary>What a run actually did.</summary>
public sealed record OrganiseReport
{
    public required OrganiseAction Action { get; init; }

    /// <summary>Files the operation completed.</summary>
    public int Succeeded { get; init; }

    /// <summary>Files it tried and could not. Named individually in <see cref="Failures"/>.</summary>
    public int Failed { get; init; }

    /// <summary>Files the plan had already decided to leave alone.</summary>
    public int Skipped { get; init; }

    /// <summary>True when the run stopped because it was cancelled. Whatever had already finished
    /// stays finished - a cancelled copy is not rolled back, it is simply not continued.</summary>
    public bool Cancelled { get; init; }

    public IReadOnlyList<OrganiseFailure> Failures { get; init; } = [];

    /// <summary>Files that now exist and did not before - the arrivals an index has to learn about.</summary>
    public IReadOnlyList<string> AddedPaths { get; init; } = [];

    /// <summary>Files that no longer exist where they were - the departures an index has to learn
    /// about. Empty for a copy, which takes nothing away.</summary>
    public IReadOnlyList<string> RemovedPaths { get; init; } = [];

    public long BytesDone { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>One line for the window and the log.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4)
        {
            $"{ByteSize.Count(Succeeded)} {OrganiseText.Past(Action)}",
        };
        if (Skipped > 0) parts.Add($"{ByteSize.Count(Skipped)} left alone");
        if (Failed > 0) parts.Add($"{ByteSize.Count(Failed)} failed");
        if (Cancelled) parts.Add("stopped");
        return string.Join(" - ", parts);
    }
}

/// <summary>
/// The thin adapter that carries out an <see cref="OrganisePlan"/> - the only class here that
/// writes, moves or deletes anything. Every decision was already made by
/// <see cref="OrganisePlanner"/>; this one just does what the preview promised.
///
/// <para><b>A move across volumes is copy - verify - remove, in that order.</b> The source is taken
/// away only once its bytes are confirmed at the destination. The copy lands under a temporary name
/// with no audio extension, so a half-finished file can never be mistaken for a track by the library
/// index or by the file pane; it is checked and only then renamed into place, which on one volume is
/// atomic. A failed or cancelled copy leaves the source completely untouched and takes the temporary
/// file with it.</para>
///
/// <para><b>How the verify works, and what it costs.</b> The source is hashed WHILE it is copied -
/// the bytes are already in the buffer, so that half is free. The destination is then read back and
/// hashed, and the two digests plus the length must match. That read-back is the only real cost, and
/// it is spent only where a delete follows: a cross-volume MOVE. A COPY checks the length and stops
/// there, because nothing is removed afterwards, so a bad copy costs a re-copy and never a track.
/// A same-volume move is a rename - no bytes move, so there is nothing to verify.</para>
///
/// <para><b>The source of a verified move is removed outright, not recycled.</b> That is not a
/// deletion: the bytes are proven present at the destination. Recycling them instead would make
/// every move need twice the space it should and would fill the bin with thousands of tracks that
/// are not missing. The DELETE action, which genuinely takes a file away, always goes to the bin and
/// refuses when there is none.</para>
///
/// <para><b>A failure never stops the run.</b> The file is recorded by name and the next one starts,
/// the same rule <c>AnalysisBatchRunner</c> follows. A cancelled run RETURNS a report rather than
/// throwing: "8 of 40 moved" is information she needs, and an exception would throw the counts away
/// with it - including the list of paths an index still has to be told about.</para>
/// </summary>
public sealed class OrganiseRunner
{
    /// <summary>1 MB. Big enough that an SMB round trip is not the bottleneck, small enough that
    /// cancelling is felt immediately and the progress ring keeps moving inside one big file.</summary>
    private const int CopyBufferBytes = 1024 * 1024;

    /// <summary>Never report more often than this. A 60 MB file over SMB would otherwise raise a
    /// few hundred progress events a second for a ring nobody can see move that fast.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>The prefix of a copy that is still landing. No audio extension, and dot-prefixed, so
    /// neither the library enumeration nor the file pane can mistake it for a track.</summary>
    private const string PartialPrefix = ".just-part-";

    private readonly IRecycleBin _recycleBin;
    private readonly Action<string>? _log;

    /// <param name="recycleBin">Where a DELETE sends its files. <see cref="NoRecycleBin"/> makes
    /// every delete refuse, which is the correct behaviour on a platform without one.</param>
    /// <param name="log">Optional diagnostic sink. A bad file must never crash a run, so failures
    /// are recorded and logged, not thrown.</param>
    public OrganiseRunner(IRecycleBin recycleBin, Action<string>? log = null)
    {
        _recycleBin = recycleBin ?? NoRecycleBin.Instance;
        _log = log;
    }

    /// <summary>
    /// Carry out <paramref name="plan"/>. Runs one file at a time on purpose: these are large
    /// sequential reads and writes over one link, and running four of them at once makes a NAS
    /// slower, not faster - unlike analysis, where the cost is CPU.
    /// </summary>
    public async Task<OrganiseReport> RunAsync(
        OrganisePlan plan,
        IProgress<OrganiseProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var items = plan.Items.Where(i => i.WillRun).ToList();
        var skipped = plan.Items.Count - items.Count;
        var bytesTotal = items.Sum(i => i.Bytes);

        var failures = new List<OrganiseFailure>();
        var added = new List<string>();
        var removed = new List<string>();

        var done = 0;
        long bytesDone = 0;
        var cancelled = false;
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        void Report(string? current, long extraBytes, bool verifying, bool force)
        {
            if (progress is null) return;
            if (!force && watch.Elapsed - lastReport < ReportInterval) return;

            lastReport = watch.Elapsed;
            progress.Report(new OrganiseProgress(
                done, items.Count, bytesDone + extraBytes, bytesTotal, current, verifying));
        }

        ReportChunk? reporter = progress is null ? null : Report;
        Report(null, 0, verifying: false, force: true);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            Report(item.SourcePath, 0, verifying: false, force: true);

            try
            {
                await RunOneAsync(plan.Action, item, reporter, ct).ConfigureAwait(false);

                if (plan.Action != OrganiseAction.Delete) added.Add(item.DestinationPath);
                if (plan.Action != OrganiseAction.Copy) removed.Add(item.SourcePath);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                // (!) Never leave a song behind, even when the file it belongs to failed. A move can
                // fail AFTER the bytes have landed - the source becoming locked between the verify
                // and the remove - and that still means there is a new file on disk, and possibly a
                // path that is no longer where it was. Both are a stat away, and the alternative is
                // a track that exists but is in no index until the next full sweep.
                if (plan.Action != OrganiseAction.Delete
                    && item.DestinationPath is { Length: > 0 } landed && File.Exists(landed))
                    added.Add(landed);

                if (plan.Action != OrganiseAction.Copy && !File.Exists(item.SourcePath))
                    removed.Add(item.SourcePath);

                failures.Add(new OrganiseFailure(item.SourcePath, ex.Message));
                _log?.Invoke($"[organise] {item.SourcePath}: {ex.Message}");
            }

            done++;
            bytesDone += item.Bytes;
            Report(item.SourcePath, 0, verifying: false, force: true);
        }

        watch.Stop();

        return new OrganiseReport
        {
            Action       = plan.Action,
            Succeeded    = done - failures.Count,
            Failed       = failures.Count,
            Skipped      = skipped,
            Cancelled    = cancelled,
            Failures     = failures,
            AddedPaths   = added,
            RemovedPaths = removed,
            BytesDone    = bytesDone,
            Elapsed      = watch.Elapsed,
        };
    }

    // -- One file --------------------------------------------------------------------------------

    private delegate void ReportChunk(string? current, long extraBytes, bool verifying, bool force);

    private async Task RunOneAsync(
        OrganiseAction action, OrganiseItem item, ReportChunk? report, CancellationToken ct)
    {
        switch (action)
        {
            case OrganiseAction.Delete:
                if (!_recycleBin.IsAvailable)
                    throw new PlatformNotSupportedException(
                        "There is no recycle bin on this system, and a track is never deleted for good.");
                _recycleBin.Send(item.SourcePath);
                return;

            case OrganiseAction.Copy:
                await CopyThenPlaceAsync(item, verify: false, report, ct).ConfigureAwait(false);
                return;

            case OrganiseAction.Move:
                await MoveAsync(item, report, ct).ConfigureAwait(false);
                return;

            default:
                throw new NotSupportedException($"Unknown organise action: {action}");
        }
    }

    private async Task MoveAsync(OrganiseItem item, ReportChunk? report, CancellationToken ct)
    {
        // The volume question is asked again here rather than trusted from the plan: a mount point
        // can change under a window that has been open for a while, and getting it wrong the safe
        // way (copy - verify - remove where a rename would have done) only costs time.
        var sameVolume =
            SystemFileProbe.VolumeKey(item.SourcePath) is { } from &&
            SystemFileProbe.VolumeKey(item.DestinationPath) is { } to &&
            string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            try
            {
                // overwrite:false - a name that appeared since the preview must throw, not win.
                File.Move(item.SourcePath, item.DestinationPath, overwrite: false);
                return;
            }
            catch (IOException)
            {
                // The volume guess was wrong (a junction, a mapped drive), or the rename is not
                // allowed here. Fall through to the slow, verified path rather than give up.
                if (File.Exists(item.DestinationPath)) throw;
            }
        }

        await CopyThenPlaceAsync(item, verify: true, report, ct).ConfigureAwait(false);

        // Only now, with the bytes confirmed at the destination, does the original go. See the class
        // doc for why this is a plain remove and not a trip to the recycle bin.
        File.Delete(item.SourcePath);
    }

    /// <summary>
    /// Copy to a temporary name in the destination folder, optionally verify it byte for byte, then
    /// rename it into place. The destination path only ever appears once the file behind it is
    /// whole.
    /// </summary>
    private async Task CopyThenPlaceAsync(
        OrganiseItem item, bool verify, ReportChunk? report, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(item.DestinationPath)
                     ?? throw new IOException("The destination has no folder.");

        var partial = Path.Combine(folder, PartialPrefix + Guid.NewGuid().ToString("N")[..12]);
        var placed = false;

        try
        {
            var (bytes, digest) = await CopyAsync(item, partial, verify, report, ct).ConfigureAwait(false);

            var landed = new FileInfo(partial);
            if (!landed.Exists || landed.Length != bytes)
                throw new IOException(
                    $"The copy is {(landed.Exists ? landed.Length : 0):N0} bytes where the original is {bytes:N0}.");

            if (verify)
            {
                report?.Invoke(item.SourcePath, 0, true, true);
                var copied = await HashAsync(partial, ct).ConfigureAwait(false);
                if (!digest.AsSpan().SequenceEqual(copied))
                    throw new IOException("The copy does not match the original byte for byte.");
            }

            File.Move(partial, item.DestinationPath, overwrite: false);
            placed = true;
        }
        finally
        {
            // A failed or cancelled copy leaves nothing behind - and once it is placed, that path is
            // the destination, so it must survive.
            if (!placed)
                try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Stream the file across, hashing the source as it goes when the caller is going to verify -
    /// the bytes are already in the buffer, so that half of the check costs nothing extra.
    /// </summary>
    private static async Task<(long Bytes, byte[] Digest)> CopyAsync(
        OrganiseItem item, string partial, bool hash, ReportChunk? report, CancellationToken ct)
    {
        using var hasher = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = new byte[CopyBufferBytes];
        long copied = 0;

        await using (var source = new FileStream(
                         item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(
                         partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         CopyBufferBytes, FileOptions.Asynchronous))
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hasher?.AppendData(buffer, 0, read);

                copied += read;
                report?.Invoke(item.SourcePath, copied, false, false);
            }

            await target.FlushAsync(ct).ConfigureAwait(false);
        }

        return (copied, hasher?.GetCurrentHash() ?? []);
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }
}
