using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using ManagedBass.Enc;

namespace JustPlay.Audio.Bass;

/// <summary>
/// <see cref="IBroadcastService"/> implementation backed by un4seen BASSenc + LAME.
///
/// Architecture:
///   1. <c>ConnectAsync</c> starts the in-process BASSenc_MP3 (LAME) encoder on the mixer's
///      output channel via <c>BASS_Encode_MP3_Start</c> (bassenc_mp3.dll — no external lame.exe).
///   2. Then calls <c>BassEnc.CastInit</c> to open the Icecast source-client connection
///      on top of that encoder handle.
///   3. The mixer (BassAudioEngine) keeps running continuously (MixerNonStop), so the
///      encoder keeps receiving audio even between track changes — the stream never drops.
///   4. <c>UpdateNowPlayingAsync</c> sends an ICY out-of-band metadata update via
///      <c>BassEnc.CastSetTitle</c>.
///
/// ManagedBass.Enc API signatures verified at managedbass.github.io/api/ManagedBass.Enc.BassEnc.html:
///   BassEnc.EncodeStart(int Handle, string CommandLine, EncodeFlags Flags,
///       EncodeProcedure? Procedure, IntPtr User) → int (encoder handle)
///   BassEnc.CastInit(int Handle, string Server, string Password, string Content,
///       string Name, string Url, string Genre, string Description,
///       string Headers, int Bitrate, bool Public) → bool
///   BassEnc.CastSetTitle(int Handle, string Title, string Url) → bool
///   BassEnc.EncodeStop(int Handle) → bool
///   BassEnc.EncodeSetNotify(int Handle, EncodeNotifyProcedure Procedure, IntPtr User) → bool
///
/// CastInit server string format (verified at un4seen.com/doc/bassenc):
///   Icecast: "host:port/mount"  (e.g. "stream.example.com:8000/live.mp3")
/// BASS_ENCODE_CAST_PUT flag: use PUT method (Icecast ≥ 2.4). In ManagedBass,
///   this is represented by passing Public=true which maps to BASS_ENCODE_CAST_PUBLIC;
///   unfortunately the ManagedBass wrapper does not expose a separate PUT flag — see note below.
///
/// Encoder note: MP3 is encoded in-process by bassenc_mp3.dll via <c>BASS_Encode_MP3_Start</c>
///   (LAME-style options, "-b {kbps}" = CBR). No external lame.exe / subprocess. Native deps
///   required at runtime: bass.dll, bassmix.dll, bassenc.dll, bassenc_mp3.dll (native/win-x64).
///
/// PUT vs SOURCE note: ManagedBass.Enc 4.0.2's CastInit wrapper has a bool 'Public'
///   parameter (maps to BASS_ENCODE_CAST_PUBLIC in the C API). The BASS_ENCODE_CAST_PUT
///   flag (SELECT PUT method) is NOT exposed through this wrapper — the C API takes a
///   DWORD flags where multiple bits can be set. Until ManagedBass exposes it, PUT/SOURCE
///   selection is left to the server's own default (PUT on modern Icecast). If legacy
///   SOURCE support is needed, it can be added via a PInvoke override in a later sprint.
/// </summary>
public sealed class BassBroadcastService : IBroadcastService
{
    // The mixer source whose post-DSP output we encode. In JustPlay this is the playback
    // engine (BassAudioEngine); in JUST STREAM it is the input-capture engine
    // (BassInputCaptureEngine). We only ever read its OutputChannel — fully decoupled.
    private readonly IBassMixerSource _source;

    // Encoder handle returned by the format-selected start function. Zero when not active.
    private int _encoder;

    // Format of the active encoder — used to gate ICY now-playing (Opus has no out-of-band
    // metadata channel, so UpdateNowPlayingAsync is a no-op for Opus streams).
    private StreamFormat _activeFormat;

    // bassenc_mp3.dll — the built-in LAME MP3 encoder add-on for BASSenc. ManagedBass.Enc 4.0.2
    // ships NO wrapper for it, so we P/Invoke directly. Encodes IN-PROCESS — no external lame.exe.
    // Signature (un4seen, verified at un4seen.com/doc/bassenc_mp3/BASS_Encode_MP3_Start.html):
    //   HENCODE BASS_Encode_MP3_Start(DWORD handle, const char *options, DWORD flags,
    //                                 ENCODEPROCEX proc, void *user)
    // Returns the encoder handle (0 = fail). options are LAME-style; "-b 320" = CBR 320 kbps.
    // proc = NULL: no data callback — BASS_Encode_CastInit forwards the encoded output to Icecast.
    [DllImport("bassenc_mp3", CharSet = CharSet.Ansi)]
    private static extern int BASS_Encode_MP3_Start(int handle, string options, int flags, IntPtr proc, IntPtr user);

    // BASS_Encode_CastInit flags (verified against the bassenc.h shipped with this bassenc.dll).
    private const int BASS_ENCODE_CAST_PUBLIC = 1;  // add to public directory
    private const int BASS_ENCODE_CAST_PUT    = 2;  // use the HTTP PUT method (Icecast 2.4+)
    private const int BASS_ENCODE_CAST_SSL    = 4;  // use SSL/TLS

    // BASS_Encode_CastInit — direct P/Invoke against the NEW ABI (final arg = DWORD flags).
    // ManagedBass.Enc 4.0.2's wrapper targets the OLD ABI (final arg = BOOL pub) and therefore
    // can NEVER select PUT or SSL — it always sends the legacy SOURCE method. We call the native
    // function ourselves so the profile's PUT/SOURCE + TLS choice is honoured.
    //   BOOL BASS_Encode_CastInit(HENCODE handle, const char *server, const char *pass,
    //       const char *content, const char *name, const char *url, const char *genre,
    //       const char *desc, const char *headers, DWORD bitrate, DWORD flags)
    [DllImport("bassenc", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BASS_Encode_CastInit(int handle, string server, string pass,
        string content, string? name, string? url, string? genre, string? desc, string? headers,
        int bitrate, int flags);

    private BroadcastState _state = BroadcastState.Disconnected;

    // Detail of the most recent failure (BASS error + step) for UI diagnostics.
    private string? _lastError;

    // Pin the notify delegate to prevent GC collection while BASS holds the native pointer.
    // (CallbackOnCollectedDelegate risk — same pattern as BassAudioEngine._endSync.)
    private EncodeNotifyProcedure? _notifyProc;

    // ── Auto-reconnect (the Advanced-tab promise, built 2026-07-05) ───────────────────────
    // When the cast connection (or the encoder feeding it) dies, we retry the LAST profile
    // with exponential backoff — 2/4/8/16 s, then capped at 30 s (Chloe: 60 was too slow for
    // the club case) — INDEFINITELY, until a retry succeeds or the user hits DISCONNECT. Between attempts the public state stays
    // Reconnecting (no Error flicker); every attempt is visible in the console log. A running
    // set recording is a separate encoder on the mixer and is never touched by any of this.
    private StreamServerProfile? _lastProfile;
    private CancellationTokenSource? _reconnectCts;
    private readonly object _reconnectSync = new(); // guards _reconnectCts start/cancel
    private readonly object _connectSync = new();   // serializes ConnectCore/StopEncoder across threads

    public BassBroadcastService(IBassMixerSource source)
    {
        _source = source;
    }

    public BroadcastState State => _state;

    public string? LastError => _lastError;

    public event EventHandler<BroadcastState>? StateChanged;

    /// <inheritdoc/>
    /// <remarks>
    /// Threading: called from the UI thread (via an async RelayCommand). All BASS calls
    /// are synchronous P/Invokes; the connecting work is cheap enough to stay inline
    /// (no TCP dial-up here — BassEnc.CastInit does its own socket work internally).
    /// On failure the state is set to Error; the caller reads Bass.LastError.
    /// </remarks>
    public Task ConnectAsync(StreamServerProfile profile, CancellationToken ct = default)
    {
        CancelReconnect();      // a user-initiated (re)connect supersedes any pending auto-retry
        _lastProfile = profile; // remembered for auto-reconnect after a dropped connection
        lock (_connectSync)
        {
            try
            {
                ConnectCore(profile, ct, fromReconnect: false);
            }
            catch (Exception ex)
            {
                // A throw out of ConnectCore (e.g. DllNotFoundException when a BASS add-on
                // library can't be loaded — hit on macOS while the enc dylibs still had
                // unresolvable @rpath deps) would escape into the async RelayCommand and
                // leave the UI stuck on "Connecting…" with no Error state and no log entry.
                _lastError = ex.Message;
                Console.WriteLine($"[Broadcast] Connect failed: {ex}");
                SetState(BroadcastState.Error);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The actual connect sequence (encoder → notify → CastInit). Returns true when Connected.
    /// <paramref name="fromReconnect"/> suppresses the Error state on failure: during
    /// auto-reconnect the public state stays <see cref="BroadcastState.Reconnecting"/> between
    /// attempts (no Error flicker in the UI); the failure detail still goes to the console log.
    /// </summary>
    private bool ConnectCore(StreamServerProfile profile, CancellationToken ct, bool fromReconnect)
    {
        // Ensure we start clean.
        if (_encoder != 0)
            StopEncoder();

        _lastError = null;
        SetState(BroadcastState.Connecting);

        var mixer = _source.OutputChannel;
        if (mixer == 0)
        {
            // The mixer is created lazily by the source (first Load for playback, or capture
            // start for JUST STREAM). Connecting before there is any audio is a rare edge case;
            // surface it as an error.
            _lastError = "No audio source yet — start playback (JustPlay) or capture (JUST STREAM) before connecting.";
            if (!fromReconnect) SetState(BroadcastState.Error);
            Console.WriteLine("[Broadcast] " + _lastError);
            return false;
        }

        if (ct.IsCancellationRequested)
        {
            SetState(BroadcastState.Disconnected);
            return false;
        }

        // ── Step 1: Start the encoder — MP3 (LAME) or Ogg/Opus ─────────────
        // MP3: bassenc_mp3.dll (LAME, in-process, LAME-style options: "-b {kbps}" = CBR).
        // Opus: bassenc_opus.dll (libopus, in-process). Options are opusenc-style:
        //   "--bitrate {kbps}" = CBR bitrate (verified at un4seen.com/doc/bassenc_opus/
        //   BASS_Encode_OPUS_Start.html). libopus resamples the mixer's 44100 Hz output
        //   to 48 kHz internally — this is transparent and correct on x64.
        // In both cases the encoder taps the mixer output; CastInit (Step 3) routes it to Icecast.
        _activeFormat = profile.Format;
        if (profile.Format == StreamFormat.Opus)
        {
            var opusOptions = $"--bitrate {profile.BitrateKbps}";
            _encoder = BassEnc_Opus.Start(mixer, opusOptions, EncodeFlags.Default, (EncodeProcedure?)null, IntPtr.Zero);
            if (_encoder == 0)
            {
                _lastError = $"Opus encoder failed to start ({ManagedBass.Bass.LastError}). Check bassenc_opus.dll.";
                if (!fromReconnect) SetState(BroadcastState.Error);
                Console.WriteLine("[Broadcast] " + _lastError);
                return false;
            }
        }
        else
        {
            // MP3 (default): LAME-style "-b {kbps}" = CBR bitrate.
            var mp3Options = $"-b {profile.BitrateKbps}";
            _encoder = BASS_Encode_MP3_Start(mixer, mp3Options, 0, IntPtr.Zero, IntPtr.Zero);
            if (_encoder == 0)
            {
                _lastError = $"MP3 encoder failed to start ({ManagedBass.Bass.LastError}). Check bassenc_mp3.dll.";
                if (!fromReconnect) SetState(BroadcastState.Error);
                Console.WriteLine("[Broadcast] " + _lastError);
                return false;
            }
        }

        // ── Step 2: Register connection-dropped notification ──────────────
        // EncodeNotifyStatus.CastServerConnectionDied → transition to Error/Reconnecting.
        // The delegate is pinned as a field (GC safety).
        _notifyProc = (_, status, _) =>
        {
            // Called on a BASS internal thread — do NOT call UI APIs here.
            if (status == EncodeNotifyStatus.CastServerConnectionDied ||
                status == EncodeNotifyStatus.EncoderDied)
            {
                _lastError = $"Connection lost ({status}).";
                Console.WriteLine("[Broadcast] " + _lastError);
                // Auto-reconnect instead of giving up: EncoderDied is included deliberately —
                // the retry rebuilds encoder + cast from scratch either way, and a permanent
                // local failure just keeps backing off at 60 s until the user disconnects.
                BeginReconnect();
            }
        };
        BassEnc.EncodeSetNotify(_encoder, _notifyProc);

        // ── Step 3: Open the Icecast source-client connection ────────────
        // Server string format: "host:port/mount"  (Icecast)
        // Content type: BassEnc.MimeMp3 = "audio/mpeg"
        // Public=false: do not advertise to the server's public directory listing.
        // Source: un4seen.com/doc/bassenc BASS_Encode_CastInit docs.
        var server = $"{profile.Host}:{profile.Port}{profile.Mount}";

        // Auth: BASS already assumes the username "source" when ONLY a password is given —
        // exactly what standard Icecast and BUTT send (source / password). So for the default
        // "source" user we pass the bare password; prepending "source:" ourselves can be
        // mis-parsed → BASS_ERROR_CAST_DENIED. Only prepend a username for NON-default users.
        var creds = (string.IsNullOrWhiteSpace(profile.Username) ||
                     string.Equals(profile.Username, "source", StringComparison.OrdinalIgnoreCase))
            ? profile.Password
            : $"{profile.Username}:{profile.Password}";

        // Method flags. CRITICAL: we call BASS_Encode_CastInit DIRECTLY (P/Invoke), NOT
        // BassEnc.CastInit — ManagedBass.Enc 4.0.2 targets the OLD ABI whose final arg is a
        // BOOL 'pub', so it can ONLY ever send the legacy SOURCE method. Our bassenc.dll uses
        // the NEW ABI: final arg is DWORD flags (PUBLIC=1 / PUT=2 / SSL=4, verified against the
        // bassenc.h shipped with this DLL). Modern Icecast (2.4+) commonly requires the PUT
        // method — sending SOURCE returns BASS_ERROR_CAST_DENIED ("'pass' is not valid").
        // AUTO method: always try the modern PUT method first; the fallback below retries with the
        // legacy SOURCE method if PUT is denied. This covers every Icecast version/host without the
        // user having to pick — so the per-profile Protocol toggle was removed from the UI.
        var flags = BASS_ENCODE_CAST_PUT;
        if (profile.UseTls) flags |= BASS_ENCODE_CAST_SSL;

        Console.WriteLine($"[Broadcast] Connecting to {server} via PUT (auto; SOURCE fallback on denial)…");

        // Content MIME type: Icecast sets this as the stream's Content-Type header and uses it
        // to determine the container/codec. MP3 = "audio/mpeg" (ICY out-of-band metadata works).
        // Ogg/Opus = "audio/ogg" (no out-of-band ICY metadata; see UpdateNowPlayingAsync guard).
        var contentType = profile.Format == StreamFormat.Opus ? "audio/ogg" : "audio/mpeg";

        var castOk = BASS_Encode_CastInit(
            _encoder,
            server,
            creds,
            contentType,
            profile.Name,           // stream name
            null,                   // url
            null,                   // genre
            null,                   // description
            null,                   // headers
            profile.BitrateKbps,
            flags);

        // Auto-fallback: if PUT was denied, some Icecast setups only accept the legacy SOURCE
        // method. Retry once without the PUT flag so the user doesn't need to know which to pick.
        if (!castOk && (flags & BASS_ENCODE_CAST_PUT) != 0)
        {
            var putErr = ManagedBass.Bass.LastError;
            Console.WriteLine($"[Broadcast] PUT method failed ({putErr}); retrying with SOURCE…");
            flags &= ~BASS_ENCODE_CAST_PUT;
            castOk = BASS_Encode_CastInit(
                _encoder, server, creds, contentType, profile.Name,
                null, null, null, null, profile.BitrateKbps, flags);
        }

        if (!castOk)
        {
            var err = ManagedBass.Bass.LastError;
            StopEncoder();
            // Icecast returns 403 (→ CastDenied) for BOTH a bad source password AND a mount
            // that is already in use. Spell out both so we don't chase the wrong one again.
            _lastError = err.ToString() == "CastDenied"
                ? $"Denied by {server}: wrong source password, or mount '{profile.Mount}' is already in use (disconnect any other encoder first)."
                : $"Cast to {server} failed: {err}";
            if (!fromReconnect) SetState(BroadcastState.Error);
            Console.WriteLine("[Broadcast] " + _lastError);
            return false;
        }

        SetState(BroadcastState.Connected);
        Console.WriteLine($"[Broadcast] Connected to {server}");
        return true;
    }

    /// <inheritdoc/>
    public Task DisconnectAsync()
    {
        CancelReconnect(); // user says stop — that includes any pending auto-retry
        lock (_connectSync)
            StopEncoder();
        SetState(BroadcastState.Disconnected);
        return Task.CompletedTask;
    }

    // ── Auto-reconnect machinery ─────────────────────────────────────────

    /// <summary>Enter Reconnecting and start the retry loop (no-op if one is already running).
    /// Called from the BASS notify thread — must not block.</summary>
    private void BeginReconnect()
    {
        lock (_reconnectSync)
        {
            if (_reconnectCts is { IsCancellationRequested: false })
                return; // a loop is already at work
            if (_lastProfile is null)
            {
                SetState(BroadcastState.Error); // nothing to reconnect TO — shouldn't happen
                return;
            }
            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            SetState(BroadcastState.Reconnecting);
            _ = Task.Run(() => ReconnectLoopAsync(_lastProfile, cts.Token));
        }
    }

    /// <summary>
    /// Retry with exponential backoff — 2/4/8/16 s, then capped at 30 s — indefinitely,
    /// until an attempt succeeds or <see cref="CancelReconnect"/> (user CONNECT/DISCONNECT)
    /// stops the loop. Failed attempts stay in Reconnecting; details go to the console log.
    /// </summary>
    private async Task ReconnectLoopAsync(StreamServerProfile profile, CancellationToken ct)
    {
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
            Console.WriteLine($"[Broadcast] Reconnect attempt {attempt} in {delay.TotalSeconds:0} s…");
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;

            bool ok;
            lock (_connectSync)
            {
                try
                {
                    ok = ConnectCore(profile, ct, fromReconnect: true);
                }
                catch (Exception ex)
                {
                    // Same guard as ConnectAsync: a throwing attempt is a FAILED attempt,
                    // not an unobserved-task crash from the background loop.
                    _lastError = ex.Message;
                    Console.WriteLine($"[Broadcast] Reconnect attempt failed: {ex.Message}");
                    ok = false;
                }
            }
            if (ok)
            {
                Console.WriteLine("[Broadcast] Reconnected.");
                return;
            }
            SetState(BroadcastState.Reconnecting); // stay visibly reconnecting between attempts
        }
    }

    private void CancelReconnect()
    {
        lock (_reconnectSync)
        {
            _reconnectCts?.Cancel();
            _reconnectCts = null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Safe to call when not connected — silently discarded.
    /// BassEnc.CastSetTitle sends the ICY out-of-band metadata GET request to Icecast.
    /// The second parameter (url) is the optional song-URL — we don't have one.
    /// Source: managedbass.github.io/api/ManagedBass.Enc.BassEnc.html — CastSetTitle.
    /// </remarks>
    public Task UpdateNowPlayingAsync(string title)
    {
        if (_state != BroadcastState.Connected || _encoder == 0)
            return Task.CompletedTask;

        // Ogg/Opus streams have no out-of-band ICY metadata channel — CastSetTitle only works
        // for MP3/ICY streams. Documented at IBroadcastService.cs:88-89 and in streaming-broadcast.md §1.3B.
        if (_activeFormat == StreamFormat.Opus)
            return Task.CompletedTask;

        var ok = BassEnc.CastSetTitle(_encoder, title, null);
        if (!ok)
            Console.WriteLine($"[Broadcast] CastSetTitle failed: {ManagedBass.Bass.LastError}");

        return Task.CompletedTask;
    }

    private void StopEncoder()
    {
        if (_encoder == 0) return;
        BassEnc.EncodeStop(_encoder);
        _encoder = 0;
        _notifyProc = null;
    }

    private void SetState(BroadcastState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
