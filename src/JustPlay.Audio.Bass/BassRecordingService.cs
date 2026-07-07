using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using ManagedBass;
using ManagedBass.Enc;

namespace JustPlay.Audio.Bass;

/// <summary>
/// <see cref="IRecordingService"/> implementation backed by un4seen BASSenc — "record your
/// set" for JUST STREAM.
///
/// Architecture: this starts a SECOND, fully independent in-process encoder directly on
/// <see cref="IBassMixerSource.OutputChannel"/> — the SAME persistent mixer output the
/// broadcast encoder (<see cref="BassBroadcastService"/>) taps — via its own
/// <c>BASS_Encode_&lt;Codec&gt;_StartFile</c> handle. A BASS mixer output can drive any
/// number of independent encoder taps simultaneously; this is not a tee of the cast
/// encoder's bytes, it is its own encode session with its own handle, its own pinned notify
/// delegate, and its own state machine. There is ZERO shared state with
/// <see cref="BassBroadcastService"/>.
///
/// HARD INVARIANT (see IRecordingService.cs remarks): a recording failure — disk full,
/// unreachable NAS folder, encoder death — must NEVER interrupt or degrade the broadcast.
/// This class never calls into <see cref="BassBroadcastService"/>, never throws across the
/// <see cref="IRecordingService"/> boundary, and every failure path here only ever mutates
/// recording-local fields (<c>_encoder</c>, <c>_state</c>, <c>_lastError</c>).
///
/// Native deps (native/win-x64, see the DLL comment block in JustPlay.Audio.Bass.csproj):
///   bassenc.dll (BASS_Encode_StartPCMFile — WAV/AIFF), bassenc_mp3.dll (MP3, LAME),
///   bassenc_opus.dll (Opus, libopus), bassenc_flac.dll (FLAC — recording-only add-on).
/// </summary>
public sealed class BassRecordingService : IRecordingService
{
    // The same mixer-output source the broadcast service reads. We only ever read
    // OutputChannel — fully decoupled, see the class doc's hard invariant.
    private readonly IBassMixerSource _source;

    // Encoder handle returned by the format-selected StartFile function. Zero when idle.
    private int _encoder;

    private RecordingState _state = RecordingState.Idle;

    // Detail of the most recent failure (BASS error + step) for UI diagnostics.
    private string? _lastError;

    // Full path of the file currently (or most recently) being written. Deliberately NOT
    // cleared on StopAsync — see IRecordingService.CurrentFilePath ("saved to …" in the UI).
    private string? _currentFilePath;

    // Pin the notify delegate to prevent GC collection while BASS holds the native function
    // pointer for the lifetime of the encoder (CallbackOnCollectedDelegate risk — same pattern
    // as BassBroadcastService._notifyProc / BassAudioEngine._endSync).
    private EncodeNotifyProcedure? _notifyProc;

    // ── Auto-trim silence gate (Chloe, 2026-07-04: "erst online, dann Musik — 10-15 min
    // Stille am Anfang… und am Ende" + "diese Stille ist dann aufgezeichnet — ob wir wollen
    // oder nicht") ────────────────────────────────────────────────────────────────────────
    // Silence is never WRITTEN — including the tail. The naive approach (pause the encoder
    // after N s of silence) still records those N s before the pause; her follow-up killed it.
    // Instead the encoder runs permanently PAUSED (= BASS never auto-feeds it) and we feed it
    // MANUALLY via BASS_Encode_Write from our own DSP tap on the mixer, through a LOOK-BEHIND
    // ring buffer of TailHoldSeconds:
    //   · signal        → flush any held audio + the live block straight through (file ~live)
    //   · short silence → held back in the ring; if music returns within the hold, the break
    //                     is flushed 1:1 (deliberate mix pauses are NEVER collapsed)
    //   · long silence  → the ring is dropped, the gap is CUT from the file entirely
    //   · stop          → whatever silence sits in the ring is discarded, never flushed —
    //                     the file ends on the last beat, with ZERO trailing dead air.
    // Leading silence starts in "gap" state, so the file also begins on the first beat.
    // BASS_Encode_SetPaused doc (un4seen.com/doc/bassenc/BASS_Encode_SetPaused.html) states
    // paused encoders receive no DSP data but CAN be fed via BASS_Encode_Write — that
    // combination is what makes this possible. Identical behaviour for all five codecs.
    //
    // The gate runs inside the mixer's DSP chain (BASS mixer thread): sample-accurate,
    // independent of the UI render loop (which throttles when the window is minimized —
    // exactly when a DJ is playing). No locks, no allocation in the callback.
    private readonly object _gateSync = new();
    private readonly Stopwatch _written = new(); // RecordedDuration for the UNGATED mode only
    private bool _trimSilence;
    private volatile bool _gateEverOpened; // any audio written (drives ARMED display + empty-file discard)

    // DSP-thread state (touched only from GateDsp between install and removal).
    private ManagedBass.DSPProcedure? _dspProc; // pinned — same GC rationale as _notifyProc
    private int _dspHandle;
    private int _dspMixer;      // mixer the DSP was installed on (for removal)
    private float[]? _ring;     // look-behind buffer, capacity = hold + 1 s slack
    private int _ringStart, _ringCount;
    private long _silenceRun;   // samples of continuous silence so far
    private bool _inGap;        // true = current silence is a decided gap (or leading) → discard
    private long _samplesWritten; // total samples fed to the encoder (Volatile) → RecordedDuration
    private int _freq, _chans;

    // -54 dBFS peak = true silence (digital dead air, faders down), safely below any audible
    // content — a quiet outro or reverb tail never trips it. Deliberately LOWER than the UI's
    // -48 dBFS "signal present" pulse threshold: the gate must be the more conservative of the two.
    private const double SilenceThresholdPeak = 0.002;

    // How long a silence may last before it is treated as a GAP and cut. Breaks shorter than
    // this are preserved 1:1 by the look-behind buffer (the most dramatic real-world mix break
    // is ~15-20 s), so the hold length never appears in the file either way.
    private const double TailHoldSeconds = 30;

    // Our tap must see the FINAL bus signal: the capture engine's wet tap sits at -500 and
    // BASS feeds encoders at the very end of the chain (default encoder DSP priority -1000,
    // lower = later). -800 puts the gate after every sound-shaping DSP, right where the
    // auto-feed would read.
    private const int GateDspPriority = -800;

    // un4seen.com/doc/bassenc/BASS_Encode_SetPaused.html — while paused, no sample data is
    // fed to the encoder by the DSP system, but manual BASS_Encode_Write still works. Direct
    // P/Invoke for the same self-contained-auditability reason as the StartFile entry points.
    [DllImport("bassenc")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_Encode_SetPaused(int handle, [MarshalAs(UnmanagedType.Bool)] bool paused);

    // un4seen.com/doc/bassenc/BASS_Encode_Write.html — feed sample data manually (length in
    // BYTES, whole samples; format = the source channel's, i.e. float here). With the QUEUE
    // flag the call only enqueues (non-blocking — safe from the DSP callback).
    [DllImport("bassenc")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_Encode_Write(int handle, IntPtr buffer, int length);

    // BASS_ENCODE_PAUSE (bassenc.h = 32): start the encoder in the paused state — the file
    // (incl. its placeholder header) is created, but no sample data flows until we feed it.
    private const int BASS_ENCODE_PAUSE = 32;

    // ── P/Invokes: BASSenc's per-codec "StartFile" entry points ───────────────────────────
    // ManagedBass.Enc 4.0.2 ships no managed wrapper for any of these four (confirmed by
    // reflecting the assembly: BassEnc has no MP3/PCM "StartFile" member; BassEnc_Opus and
    // BassEnc_Flac DO expose a managed "Start(handle, options, flags, filename)" overload,
    // but we P/Invoke directly here for all four codecs — MP3 and WAV/AIFF have no managed
    // wrapper at all, so raw P/Invoke was required anyway, and doing it uniformly keeps this
    // file self-contained and trivially auditable against the un4seen docs, mirroring
    // BassBroadcastService's direct P/Invoke of BASS_Encode_MP3_Start.
    //
    // All four return the encoder handle (0 = failure — read ManagedBass.Bass.LastError).

    // un4seen.com/doc/bassenc_mp3/BASS_Encode_MP3_StartFile.html
    // HENCODE BASS_Encode_MP3_StartFile(DWORD handle, const char *options, DWORD flags, const char *filename)
    [DllImport("bassenc_mp3", CharSet = CharSet.Ansi)]
    private static extern int BASS_Encode_MP3_StartFile(int handle, string? options, int flags, string filename);

    // un4seen.com/doc/bassenc_opus/BASS_Encode_OPUS_StartFile.html
    // HENCODE BASS_Encode_OPUS_StartFile(DWORD handle, const char *options, DWORD flags, const char *filename)
    [DllImport("bassenc_opus", CharSet = CharSet.Ansi)]
    private static extern int BASS_Encode_OPUS_StartFile(int handle, string? options, int flags, string filename);

    // un4seen.com/doc/bassenc_flac/BASS_Encode_FLAC_StartFile.html
    // HENCODE BASS_Encode_FLAC_StartFile(DWORD handle, const char *options, DWORD flags, const char *filename)
    [DllImport("bassenc_flac", CharSet = CharSet.Ansi)]
    private static extern int BASS_Encode_FLAC_StartFile(int handle, string? options, int flags, string filename);

    // un4seen.com/doc/bassenc/BASS_Encode_StartPCMFile.html
    // HENCODE BASS_Encode_StartPCMFile(DWORD handle, DWORD flags, const char *filename)
    [DllImport("bassenc", CharSet = CharSet.Ansi)]
    private static extern int BASS_Encode_StartPCMFile(int handle, int flags, string filename);

    // BASS_ENCODE_* flags used by the StartFile calls above (verified against the bassenc.h
    // shipped with these DLLs, and cross-checked by reflecting ManagedBass.Enc.EncodeFlags —
    // its ConvertFloatTo16BitInt/Queue/AIFF/Dither members carry these exact values: 4, 0x200,
    // 0x4000, 0x8000. We use raw ints here because the StartFile P/Invokes above take a plain
    // DWORD flags parameter, not the EncodeFlags enum type).
    private const int BASS_ENCODE_FP_16BIT = 4;      // convert float mixer output → 16-bit int
    private const int BASS_ENCODE_QUEUE    = 0x200;  // async feed — a slow disk (NAS!) can never block the mixer thread
    private const int BASS_ENCODE_AIFF     = 0x4000; // AIFF header instead of WAVE (StartPCMFile only)
    private const int BASS_ENCODE_DITHER   = 0x8000; // TPDF dither on the float→int conversion

    public BassRecordingService(IBassMixerSource source)
    {
        _source = source;
    }

    public RecordingState State => _state;

    public string? LastError => _lastError;

    public string? CurrentFilePath => _currentFilePath;

    /// <inheritdoc/>
    /// <remarks>
    /// ManagedBass.Enc 4.0.2's <c>BassEnc.EncodeGetCount(int, EncodeCount)</c> returns
    /// <c>long</c> and does expose an <c>EncodeCount.Out</c> member (confirmed by reflecting
    /// the assembly — no raw P/Invoke fallback needed here). Polled from a UI meter-rate
    /// timer, so any failure is swallowed rather than thrown.
    /// </remarks>
    public long BytesWritten
    {
        get
        {
            if (_state != RecordingState.Recording || _encoder == 0)
                return 0;

            try
            {
                return BassEnc.EncodeGetCount(_encoder, EncodeCount.Out);
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Gated mode counts the samples actually fed to the encoder — the file's exact
    /// length. Ungated mode uses a plain wall-clock stopwatch (everything is written anyway).</remarks>
    public TimeSpan RecordedDuration
    {
        get
        {
            if (!_trimSilence)
                return _written.Elapsed;
            long perSecond = (long)_freq * _chans;
            return perSecond > 0
                ? TimeSpan.FromSeconds(Volatile.Read(ref _samplesWritten) / (double)perSecond)
                : TimeSpan.Zero;
        }
    }

    /// <inheritdoc/>
    public bool IsWaitingForSignal => _state == RecordingState.Recording && _trimSilence && !_gateEverOpened;

    public event EventHandler<RecordingState>? StateChanged;

    /// <inheritdoc/>
    /// <remarks>
    /// Threading: called from the UI thread (via an async RelayCommand), same as
    /// BassBroadcastService.ConnectAsync. All BASS calls are synchronous P/Invokes — cheap
    /// enough (no network dial-up, just opening a local file) to run inline. Never throws;
    /// failures set State = Error and populate LastError.
    /// </remarks>
    public Task StartAsync(RecordingJob job)
    {
        // Restart semantics (per IRecordingService.StartAsync doc): calling while already
        // recording finalizes the current file first, then starts the new one.
        if (_encoder != 0)
            StopEncoder();

        _lastError = null;

        var mixer = _source.OutputChannel;
        if (mixer == 0)
        {
            // The mixer is created lazily by the capture engine. Recording before capture has
            // started is a rare edge case; surface it as an error rather than guessing.
            _lastError = "No audio source yet — start capture before recording.";
            SetState(RecordingState.Error);
            Console.WriteLine("[Recording] " + _lastError);
            return Task.CompletedTask;
        }

        // ── Step 1: make sure the target folder exists ────────────────────────────────
        // Wrapped defensively — an unreachable NAS path, a permissions issue, or a malformed
        // path must degrade to Error, never throw across the interface boundary.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.FilePath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _lastError = ex.Message;
            SetState(RecordingState.Error);
            Console.WriteLine("[Recording] Could not create recording folder: " + _lastError);
            return Task.CompletedTask;
        }

        // ── Step 2: start the codec-specific in-process encoder, writing straight to file ──
        // MP3: LAME-style "-b {kbps}" = CBR (mirrors BassBroadcastService's MP3 options).
        // Opus: opusenc-style "--bitrate {kbps}" = CBR.
        // FLAC: cannot take float input; BASS_ENCODE_FP_16BIT + BASS_ENCODE_DITHER convert the
        //   mixer's float output to dithered 16-bit explicitly (rather than relying on any
        //   codec-specific default float handling).
        // WAV/AIFF: BASS_Encode_StartPCMFile defaults to a WAVE header; BASS_ENCODE_AIFF swaps
        //   it for an AIFF header. Same float→16-bit-dither conversion as FLAC.
        // BASS_ENCODE_QUEUE on every codec: encoding happens off the mixer thread via an async
        //   queue, so a slow NAS write can never block or glitch playback/the broadcast.
        // BASS_ENCODE_PAUSE when auto-trim is on: the recorder starts ARMED — the file is
        //   created but nothing is written until the silence gate sees the first signal.
        var gateFlags = job.TrimSilence ? BASS_ENCODE_PAUSE : 0;
        _encoder = job.Codec switch
        {
            RecordingCodec.Mp3 => BASS_Encode_MP3_StartFile(
                mixer, $"-b {job.BitrateKbps}", BASS_ENCODE_QUEUE | gateFlags, job.FilePath),

            RecordingCodec.Opus => BASS_Encode_OPUS_StartFile(
                mixer, $"--bitrate {job.BitrateKbps}", BASS_ENCODE_QUEUE | gateFlags, job.FilePath),

            RecordingCodec.Flac => BASS_Encode_FLAC_StartFile(
                mixer, null, BASS_ENCODE_QUEUE | BASS_ENCODE_FP_16BIT | BASS_ENCODE_DITHER | gateFlags, job.FilePath),

            RecordingCodec.Wav => BASS_Encode_StartPCMFile(
                mixer, BASS_ENCODE_QUEUE | BASS_ENCODE_FP_16BIT | BASS_ENCODE_DITHER | gateFlags, job.FilePath),

            RecordingCodec.Aiff => BASS_Encode_StartPCMFile(
                mixer, BASS_ENCODE_QUEUE | BASS_ENCODE_FP_16BIT | BASS_ENCODE_DITHER | BASS_ENCODE_AIFF | gateFlags, job.FilePath),

            _ => 0,
        };

        if (_encoder == 0)
        {
            _lastError = $"{job.Codec} encoder failed to start ({ManagedBass.Bass.LastError}). Check {NativeDllName(job.Codec)}.";
            SetState(RecordingState.Error);
            Console.WriteLine("[Recording] " + _lastError);
            return Task.CompletedTask;
        }

        // ── Step 3: register the encoder-died / queue-full notification ───────────────────
        // The delegate is pinned as a field (GC safety, see _notifyProc doc above).
        _notifyProc = (_, status, _) =>
        {
            // Fires on a BASS internal thread — do NOT call UI APIs here, and NEVER reach into
            // BassBroadcastService: a recording failure must never touch the broadcast.
            if (status == EncodeNotifyStatus.EncoderDied)
            {
                _lastError = $"Recording stopped by itself — disk full or file write error? Last file: {_currentFilePath}";
                Console.WriteLine("[Recording] " + _lastError);
                // The encoder already died on BASS's side — just drop our handle/delegate,
                // do NOT call BASS_Encode_Stop on an already-dead handle.
                lock (_gateSync)
                {
                    _encoder = 0;
                    RemoveGateTap();
                    _written.Stop();
                }
                _notifyProc = null;
                SetState(RecordingState.Error);
            }
            else if (status == EncodeNotifyStatus.QueueFull)
            {
                // BASS_ENCODE_QUEUE's async buffer overflowed — the disk couldn't keep up and
                // some audio was dropped from the FILE. The mixer/broadcast were never blocked.
                // Not fatal: keep recording, just surface that the disk is the bottleneck.
                Console.WriteLine("[Recording] encode queue overflowed — some audio was dropped (slow disk?)");
            }
        };
        BassEnc.EncodeSetNotify(_encoder, _notifyProc);

        // ── Step 4: arm the silence gate (or run ungated) ─────────────────────────────────
        lock (_gateSync)
        {
            _trimSilence = job.TrimSilence;
            _written.Reset();
            Volatile.Write(ref _samplesWritten, 0);
            if (job.TrimSilence)
            {
                // Armed: the encoder stays PERMANENTLY paused (no auto-feed; the PAUSE start
                // flag above + belt-and-braces SetPaused) — our DSP tap feeds it manually
                // through the look-behind ring, so silence never reaches the file at all.
                _gateEverOpened = false;
                BASS_Encode_SetPaused(_encoder, true);

                var info = ManagedBass.Bass.ChannelGetInfo(mixer);
                _freq = info.Frequency;
                _chans = Math.Max(1, info.Channels);
                var holdSamples = (long)(TailHoldSeconds * _freq) * _chans;
                _ring = new float[(int)(holdSamples + (long)_freq * _chans)]; // hold + 1 s slack (≈12 MB at 48 kHz stereo)
                _ringStart = _ringCount = 0;
                _silenceRun = 0;
                _inGap = true; // leading silence is ALWAYS a gap — the file starts on the first beat

                _dspProc = GateDsp;
                _dspMixer = mixer;
                _dspHandle = ManagedBass.Bass.ChannelSetDSP(mixer, _dspProc, IntPtr.Zero, GateDspPriority);
                if (_dspHandle == 0)
                {
                    // Gate tap failed (shouldn't happen) — degrade to an UNGATED recording
                    // rather than a silent no-op file: un-pause the auto-feed and log it.
                    Console.WriteLine($"[Recording] Auto-trim tap failed ({ManagedBass.Bass.LastError}) — recording without trim.");
                    _trimSilence = false;
                    _dspProc = null;
                    _ring = null;
                    BASS_Encode_SetPaused(_encoder, false);
                    _gateEverOpened = true;
                    _written.Start();
                }
            }
            else
            {
                _gateEverOpened = true;
                _written.Start();
            }
        }

        var bitrateSuffix = job.BitrateKbps > 0 ? $" {job.BitrateKbps}kbps" : string.Empty;
        _currentFilePath = job.FilePath;
        SetState(RecordingState.Recording);
        Console.WriteLine(job.TrimSilence
            ? $"[Recording] Armed for {job.FilePath} ({job.Codec}{bitrateSuffix}) — writing starts with the first signal."
            : $"[Recording] Recording to {job.FilePath} ({job.Codec}{bitrateSuffix})");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The silence gate — runs INSIDE the mixer's DSP chain on the BASS mixer thread, on the
    /// exact post-limiter samples the encoders see. No locks, no allocation: peak scan, then
    /// either feed the encoder (signal / short break) or hold/discard (silence). See the gate
    /// design comment at the top of the field block.
    /// </summary>
    private unsafe void GateDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        var enc = _encoder;
        if (enc == 0 || length <= 0 || _ring is not { } ring) return;

        var n = length / 4; // float samples
        var span = new Span<float>((void*)buffer, n);
        var peak = 0f;
        foreach (var s in span)
        {
            var a = s < 0 ? -s : s;
            if (a > peak) peak = a;
        }

        if (peak >= (float)SilenceThresholdPeak)
        {
            // Signal. Any HELD silence was a short, deliberate break — flush it 1:1 first,
            // then the live block. (After a decided gap the ring is empty, so the file simply
            // continues seamlessly on this beat.)
            _silenceRun = 0;
            _inGap = false;
            FlushRing(enc, ring);
            BASS_Encode_Write(enc, buffer, length);
            Volatile.Write(ref _samplesWritten, _samplesWritten + n);
            if (!_gateEverOpened)
            {
                _gateEverOpened = true;
                Console.WriteLine("[Recording] First signal — recording begins.");
            }
        }
        else
        {
            if (_inGap) return; // leading silence or an already-decided gap → discard outright

            _silenceRun += n;
            if (_silenceRun >= (long)(TailHoldSeconds * _freq) * _chans || _ringCount + n > ring.Length)
            {
                // The break outgrew the hold window → it's a real gap. Drop the held silence;
                // none of it ever reaches the file.
                _ringStart = _ringCount = 0;
                _inGap = true;
                Console.WriteLine($"[Recording] {TailHoldSeconds:0}+ s of silence — cut from the file; resumes with the music.");
                return;
            }

            // Short silence (so far): hold it back. It is flushed 1:1 if music returns in
            // time, and silently discarded if this turns out to be the end of the set.
            RingAppend(ring, span);
        }
    }

    /// <summary>Append a block to the look-behind ring (capacity guaranteed by the caller's gap check).</summary>
    private void RingAppend(float[] ring, ReadOnlySpan<float> data)
    {
        var idx = (_ringStart + _ringCount) % ring.Length;
        var first = Math.Min(data.Length, ring.Length - idx);
        data[..first].CopyTo(ring.AsSpan(idx));
        if (first < data.Length)
            data[first..].CopyTo(ring.AsSpan(0));
        _ringCount += data.Length;
    }

    /// <summary>Flush the held ring content (a short break) to the encoder — up to two segments.</summary>
    private unsafe void FlushRing(int enc, float[] ring)
    {
        if (_ringCount == 0) return;

        var first = Math.Min(_ringCount, ring.Length - _ringStart);
        fixed (float* p = &ring[_ringStart])
            BASS_Encode_Write(enc, (IntPtr)p, first * 4);
        if (first < _ringCount)
        {
            fixed (float* p = &ring[0])
                BASS_Encode_Write(enc, (IntPtr)p, (_ringCount - first) * 4);
        }

        Volatile.Write(ref _samplesWritten, _samplesWritten + _ringCount);
        _ringStart = 0;
        _ringCount = 0;
    }

    /// <inheritdoc/>
    /// <remarks>Always completes without throwing — see IRecordingService.StopAsync doc.</remarks>
    public Task StopAsync()
    {
        StopEncoder();
        SetState(RecordingState.Idle);
        return Task.CompletedTask;
    }

    private void StopEncoder()
    {
        lock (_gateSync)
        {
            if (_encoder == 0) return;

            // Detach the gate tap FIRST — after ChannelRemoveDSP returns, no more GateDsp
            // callbacks run, so EncodeStop below can't race a manual write. Whatever silence
            // still sits in the ring is deliberately dropped: the file ends on the last beat.
            RemoveGateTap();
            _written.Stop();

            try
            {
                // Finalizes the WAVE/AIFF/FLAC headers (sample count, chunk sizes) so the file is
                // playable immediately — BASS writes placeholder headers at Start and patches them
                // on Stop. un4seen.com/doc/bassenc/BASS_Encode_Stop.html
                BassEnc.EncodeStop(_encoder);
            }
            catch
            {
                // Never throw out of a stop path — worst case the file's headers stay at their
                // placeholder values (most players still recover duration by scanning).
            }

            _encoder = 0;
            _notifyProc = null;

            // Auto-trim + the gate never opened = the file contains zero audio (only placeholder
            // headers) — discard it instead of littering the folder with 44-byte corpses. This is
            // a file WE created seconds ago and it is provably empty; say so loudly in the log.
            // CurrentFilePath is nulled so the UI reports "discarded", not "saved".
            if (_trimSilence && !_gateEverOpened && _currentFilePath is { } emptyFile)
            {
                try
                {
                    File.Delete(emptyFile);
                    Console.WriteLine($"[Recording] Nothing was recorded (no signal ever arrived) — empty file discarded: {emptyFile}");
                }
                catch
                {
                    Console.WriteLine($"[Recording] Empty recording could not be deleted (still empty, safe to remove): {emptyFile}");
                }
                _currentFilePath = null;
                return;
            }

            // CurrentFilePath is intentionally left set — the UI shows "saved to <path>".
        }
    }

    /// <summary>Remove the gate's DSP tap + drop the ring. Caller holds <see cref="_gateSync"/>.</summary>
    private void RemoveGateTap()
    {
        if (_dspHandle != 0)
        {
            try { ManagedBass.Bass.ChannelRemoveDSP(_dspMixer, _dspHandle); }
            catch { /* mixer may already be gone (sample-rate teardown) — nothing to detach */ }
        }
        _dspHandle = 0;
        _dspMixer = 0;
        _dspProc = null;
        _ring = null; // frees the ~12 MB look-behind buffer
        _ringStart = _ringCount = 0;
    }

    private void SetState(RecordingState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private static string NativeDllName(RecordingCodec codec) => codec switch
    {
        RecordingCodec.Mp3 => "bassenc_mp3.dll",
        RecordingCodec.Opus => "bassenc_opus.dll",
        RecordingCodec.Flac => "bassenc_flac.dll",
        _ => "bassenc.dll", // Wav / Aiff (BASS_Encode_StartPCMFile)
    };
}
