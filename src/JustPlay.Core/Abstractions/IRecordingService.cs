using System;
using System.Threading.Tasks;
using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Lifecycle states of a set recording.
/// </summary>
public enum RecordingState
{
    /// <summary>Not recording. Initial state and state after <see cref="IRecordingService.StopAsync"/>.</summary>
    Idle,

    /// <summary>Actively encoding the master bus to the target file.</summary>
    Recording,

    /// <summary>
    /// The recording died on its own (disk full, file write error, encoder failure).
    /// The broadcast - if any - is NOT affected; see the hard invariant below.
    /// </summary>
    Error,
}

/// <summary>
/// Contract for "record your set" - encode the post-DSP/post-limiter master bus to a local
/// file. Core-layer abstraction only; the BASS implementation lives in JustPlay.Audio.Bass.
///
/// Design (Chloe, 2026-07-04): the recorder is a SECOND, fully independent encoder on the
/// same mixer output the broadcast encoder taps - NOT a tee of the cast encoder's bytes.
/// That decouples recording from the Icecast connection (record off-air, survive
/// reconnects, choose your own format) while still capturing exactly the signal listeners
/// hear. The "hear how bad you sound at 128 kbps" self-check is preserved via the
/// <see cref="RecordingFormat.SameAsStream"/> setting, which mirrors the active profile's
/// codec + bitrate into the file.
///
/// HARD INVARIANT: a recording failure (disk full, unreachable folder, encoder death) must
/// never interrupt or degrade the broadcast. Implementations keep zero shared state with
/// <see cref="IBroadcastService"/> and never throw across this interface - failures surface
/// as <see cref="RecordingState.Error"/> + <see cref="LastError"/>.
/// </summary>
public interface IRecordingService
{
    /// <summary>Current recording lifecycle state.</summary>
    RecordingState State { get; }

    /// <summary>
    /// Human-readable detail of the most recent failure (the underlying BASS error + which
    /// step failed), or null when there is no error. Surfaced in the UI for diagnostics.
    /// </summary>
    string? LastError { get; }

    /// <summary>
    /// Full path of the file being written (while <see cref="RecordingState.Recording"/>) or
    /// the last file completed (after <see cref="StopAsync"/> - so the UI can show
    /// "saved to ..."). Null before the first recording.
    /// </summary>
    string? CurrentFilePath { get; }

    /// <summary>
    /// Bytes written to the output file so far (0 when idle). Polled by the UI at meter
    /// rate to show recording size - and to make a silently-stalled disk visible.
    /// </summary>
    long BytesWritten { get; }

    /// <summary>
    /// Duration of audio actually WRITTEN to the file - with auto-trim this freezes while
    /// the silence gate holds, so the UI clock shows the file's true length, not wall time.
    /// Keeps its final value after <see cref="StopAsync"/> (until the next start).
    /// </summary>
    TimeSpan RecordedDuration { get; }

    /// <summary>
    /// True while the recorder is ARMED but the auto-trim gate has not seen the first signal
    /// yet - nothing has been written. The UI shows this as "ARMED" on the REC control.
    /// </summary>
    bool IsWaitingForSignal { get; }

    /// <summary>
    /// Fires whenever <see cref="State"/> changes. Raised on the service's own thread -
    /// UI subscribers must marshal to the UI thread (e.g. <c>Dispatcher.UIThread.Post</c>).
    /// </summary>
    event EventHandler<RecordingState>? StateChanged;

    /// <summary>
    /// Start recording the master bus to <see cref="RecordingJob.FilePath"/>. Creates the
    /// target directory if missing. Transitions to <see cref="RecordingState.Recording"/>,
    /// or <see cref="RecordingState.Error"/> on failure (never throws).
    /// Calling while already recording stops the current file first, then starts the new one.
    /// </summary>
    /// <param name="job">The resolved recording spec (codec, bitrate, target path). Policy
    /// decisions - folder default, "same as stream" resolution, file naming - happen in the
    /// caller (see <see cref="Recording"/> helpers); the service just executes.</param>
    Task StartAsync(RecordingJob job);

    /// <summary>
    /// Stop recording and finalize the file (headers are completed on stop - the file is
    /// playable afterwards). Transitions to <see cref="RecordingState.Idle"/>.
    /// Safe to call when not recording. Always completes without throwing.
    /// </summary>
    Task StopAsync();
}
