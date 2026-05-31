using JustPlay.Core.Abstractions;
using ManagedBass;
using ManagedBass.Fx;

namespace JustPlay.Audio.Bass;

/// <summary>
/// BPM detection backed by BASS_FX's built-in algorithm
/// (<see cref="BassFx.BPMDecodeGet(int, double, double, int, BassFlags, BPMProgressProcedure, IntPtr)"/>).
///
/// The algorithm detects repeating low-frequency (&lt;250 Hz) patterns — it
/// works well on rock/pop/EDM material with a distinct bass or drum beat,
/// less well on classical, jazz, or hiphop pieces with complex/varying
/// bass patterns. May report half/double the true tempo on syncopated
/// material — that's a known limitation of the algorithm itself, not our
/// wiring.
///
/// Opens its own decode stream (separate from the playback stream owned by
/// <see cref="BassAudioEngine"/>) so analysis can run while playback
/// continues. <see cref="BassFlags.FxFreeSource"/> tells BASS_FX to free
/// the underlying decoding channel for us when analysis is done.
/// </summary>
public sealed class BassBpmDetector : IBpmDetector
{
    public BassBpmDetector()
    {
        // BASS may or may not be initialised already (BassAudioEngine does it in
        // its ctor; if the detector resolves first we have to do it ourselves).
        // Already-initialised is fine; anything else is fatal.
        if (!ManagedBass.Bass.Init())
        {
            var err = ManagedBass.Bass.LastError;
            if (err != Errors.Already)
                throw new InvalidOperationException($"BASS init failed: {err}");
        }
    }

    public double? Detect(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // Decode-only stream (no playback output). Float mono is what the BPM
        // detector expects internally; passing it explicitly avoids surprises.
        var channel = ManagedBass.Bass.CreateStream(
            filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Mono);

        if (channel == 0)
            return null;

        // Determine the file's full length in seconds so we scan the whole thing.
        // ChannelGetLength returns bytes; ChannelBytes2Seconds converts.
        var lenBytes = ManagedBass.Bass.ChannelGetLength(channel);
        var endSec = ManagedBass.Bass.ChannelBytes2Seconds(channel, lenBytes);

        if (endSec <= 0)
        {
            ManagedBass.Bass.StreamFree(channel);
            return null;
        }

        ct.ThrowIfCancellationRequested();

        // 0 for MinMaxBPM → BASS_FX defaults (45..230 BPM), which is correct for
        // typical western music. FxFreeSource frees the decode channel for us
        // once analysis completes, so no explicit StreamFree call is needed on
        // the success path.
        //
        // Procedure=null because we don't need progress reporting from this
        // synchronous call — it returns when the whole range is processed.
        var bpm = BassFx.BPMDecodeGet(
            channel,
            StartSec: 0,
            EndSec: endSec,
            MinMaxBPM: 0,
            Flags: BassFlags.FxFreeSource,
            Procedure: null!,
            User: IntPtr.Zero);

        return bpm > 0 ? bpm : null;
    }
}
