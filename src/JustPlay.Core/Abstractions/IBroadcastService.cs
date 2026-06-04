using System;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Lifecycle states of the Icecast source-client connection.
/// </summary>
public enum BroadcastState
{
    /// <summary>No active connection. Initial state and state after <see cref="IBroadcastService.DisconnectAsync"/>.</summary>
    Disconnected,

    /// <summary>TCP handshake + HTTP source-client PUT in progress.</summary>
    Connecting,

    /// <summary>Connected and streaming audio to the Icecast server.</summary>
    Connected,

    /// <summary>Connection was lost; an automatic reconnect attempt is in progress (exponential backoff).</summary>
    Reconnecting,

    /// <summary>A non-recoverable error occurred (bad credentials, server rejected mount, etc.).</summary>
    Error,
}

/// <summary>
/// Contract for the JUST STREAM Icecast broadcast service. This is the Core-layer
/// abstraction only — no implementation lives here (layering rule: Core is platform-agnostic).
///
/// S2 work: The concrete implementation lives in a new <c>JustPlay.Streaming</c> adapter
/// project (never in <c>JustPlay.App</c> directly). It will:
/// <list type="bullet">
///   <item>Open a raw TCP connection to the Icecast server using <c>System.Net.Sockets.TcpClient</c></item>
///   <item>Send the HTTP PUT (or legacy SOURCE) request + Ice-* headers per <see cref="StreamServerProfile.Protocol"/></item>
///   <item>Accept encoded audio bytes pushed by the encoder DSP and pace them at bitrate to the <c>NetworkStream</c></item>
///   <item>Push ICY now-playing updates via a separate <c>HttpClient GET /admin/metadata</c> call (MP3 only)</item>
///   <item>Handle reconnection with exponential backoff on any <c>IOException</c></item>
///   <item>Wrap the stream in <c>SslStream</c> when <see cref="StreamServerProfile.UseTls"/> is true</item>
/// </list>
///
/// See streaming-broadcast.md §1.1–1.5 and §3.3 for the full protocol detail.
/// </summary>
public interface IBroadcastService
{
    /// <summary>Current connection lifecycle state.</summary>
    BroadcastState State { get; }

    /// <summary>
    /// Fires whenever <see cref="State"/> changes. Raised on the service's own thread —
    /// UI subscribers must marshal to the UI thread (e.g. <c>Dispatcher.UIThread.Post</c>).
    /// </summary>
    event EventHandler<BroadcastState>? StateChanged;

    /// <summary>
    /// Establish a connection to the Icecast server described by <paramref name="profile"/>
    /// and begin the source-client handshake. Transitions through
    /// <see cref="BroadcastState.Connecting"/> → <see cref="BroadcastState.Connected"/>,
    /// or <see cref="BroadcastState.Error"/> on failure.
    ///
    /// Calling <see cref="ConnectAsync"/> while already <see cref="BroadcastState.Connected"/>
    /// disconnects first, then reconnects with the new profile (allows hot-swapping servers).
    /// </summary>
    /// <param name="profile">The server profile to connect to.</param>
    /// <param name="ct">Cancellation token — cancelling aborts the connect attempt and
    /// returns the service to <see cref="BroadcastState.Disconnected"/>.</param>
    Task ConnectAsync(StreamServerProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Gracefully stop streaming and close the TCP connection.
    /// Transitions to <see cref="BroadcastState.Disconnected"/>.
    /// Always completes without throwing.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Push a now-playing title update to the Icecast server. For MP3 streams this sends
    /// the ICY out-of-band <c>GET /admin/metadata?mount=...&amp;song=...</c> request (§1.3A).
    /// For Opus streams this is a no-op (Icecast does not support out-of-band metadata for
    /// Ogg/Opus — see streaming-broadcast.md §1.3B).
    ///
    /// Safe to call when <see cref="State"/> is not <see cref="BroadcastState.Connected"/> —
    /// implementations silently discard the update.
    /// </summary>
    /// <param name="title">The "now playing" string, typically "Artist - Title".</param>
    Task UpdateNowPlayingAsync(string title);
}
