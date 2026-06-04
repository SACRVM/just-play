using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Core.Models;

/// <summary>
/// Stream codec family. MP3 is the primary codec for JUST STREAM — live ICY now-playing
/// metadata and universal player compat win over Opus's per-bit quality advantage.
/// Opus is an optional later mode; it will not support live title display (Icecast cannot
/// relay out-of-band metadata for Ogg/Opus streams). See streaming-broadcast.md §1.3.
/// </summary>
public enum StreamFormat
{
    /// <summary>
    /// MP3 (CBR, default 256 kbps). <c>Content-Type: audio/mpeg</c>. Supports live ICY
    /// now-playing via the out-of-band <c>GET /admin/metadata</c> endpoint. Primary codec.
    /// </summary>
    Mp3 = 0,

    /// <summary>
    /// Ogg/Opus (VBR). <c>Content-Type: audio/ogg</c>. Optional, lower-bandwidth mode.
    /// Does NOT support live now-playing display on Icecast. Not in the first JUST STREAM cut.
    /// </summary>
    Opus = 1,
}

/// <summary>
/// HTTP method used to connect to the Icecast source endpoint.
/// See streaming-broadcast.md §1.1.
/// </summary>
public enum IcecastProtocol
{
    /// <summary>
    /// Modern PUT method (Icecast ≥ 2.4.0). Supports <c>Expect: 100-continue</c>.
    /// Preferred for all new servers. Falls back to <see cref="Source"/> on 405.
    /// </summary>
    Put = 0,

    /// <summary>
    /// Legacy SOURCE method (deprecated, kept for SHOUTcast / old-server compat).
    /// Use only if the target server does not accept PUT.
    /// </summary>
    Source = 1,
}

/// <summary>
/// A named Icecast / SHOUTcast server profile. Users can configure multiple profiles
/// (e.g. "My 3DX server", "Test server") and switch between them in one click.
///
/// This is a plain data model — no behaviour, no IO. The concrete adapter
/// (<c>JustPlay.Streaming</c>, S2) reads this and drives the TCP source-client.
/// </summary>
public sealed record StreamServerProfile
{
    /// <summary>
    /// Stable, unique identifier for this profile. A GUID-as-string assigned at creation;
    /// never changes even if the user renames the profile. Used as <see cref="UserSettings.SelectedStreamServerId"/>.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable display name, e.g. "My 3DX Club".</summary>
    public string Name { get; init; } = "My Stream";

    /// <summary>Hostname or IP address of the Icecast server, e.g. <c>stream.example.com</c>.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>Icecast port. Default Icecast port is 8000.</summary>
    public int Port { get; init; } = 8000;

    /// <summary>
    /// Mountpoint path including the leading slash, e.g. <c>/live.mp3</c>.
    /// Must end in <c>.mp3</c> for <see cref="StreamFormat.Mp3"/> or <c>.opus</c> for
    /// <see cref="StreamFormat.Opus"/> — Icecast infers Content-Type from the extension.
    /// </summary>
    public string Mount { get; init; } = "/live.mp3";

    /// <summary>
    /// Icecast source-client username. The Icecast convention is <c>source</c>;
    /// configured in the server's <c>icecast.xml</c> under <c>&lt;authentication&gt;</c>.
    /// </summary>
    public string Username { get; init; } = "source";

    // SECURITY: password is stored in plaintext in the settings file, same as BUTT does.
    // This is acceptable for a local DJ tool where the settings file lives in the user's
    // own %LOCALAPPDATA%. Future hardening: DPAPI (Windows) / Keychain (macOS) / libsecret
    // (Linux) via a platform abstraction in JustPlay.Core. Track in: S-security-1.
    /// <summary>Icecast source password. Plaintext — see the security note above.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Stream codec. <see cref="StreamFormat.Mp3"/> is the JUST STREAM default — live
    /// now-playing via ICY out-of-band metadata and universal client compatibility.
    /// </summary>
    public StreamFormat Format { get; init; } = StreamFormat.Mp3;

    /// <summary>
    /// Encoder bitrate in kbps. For MP3 CBR: 128 / 192 / 256 / 320. Default 256 kbps
    /// (Chloe's "loud and clean, sound-first" principle; bandwidth is cheap for a club stream).
    /// See streaming-broadcast.md §2.2.
    /// </summary>
    public int BitrateKbps { get; init; } = 256;

    /// <summary>Wrap the TCP connection in TLS (<c>SslStream</c>). Requires the server to have a valid certificate.</summary>
    public bool UseTls { get; init; } = false;

    /// <summary>
    /// HTTP method for the Icecast source handshake. <see cref="IcecastProtocol.Put"/> is the
    /// modern default (Icecast ≥ 2.4.0). Switch to <see cref="IcecastProtocol.Source"/> only
    /// for legacy servers that reject PUT. See streaming-broadcast.md §1.1.
    /// </summary>
    public IcecastProtocol Protocol { get; init; } = IcecastProtocol.Put;
}

/// <summary>
/// Source-generated JSON context for <see cref="StreamServerProfile"/> and <see cref="UserSettings"/>
/// streaming fields. Reflection-free / trim-AOT-safe. Used by tests and, in S2+, by the
/// streaming adapter when it needs to (de)serialize profiles independently of the settings file.
///
/// The main settings file (<c>JsonSettingsService</c>) uses its own reflection-based
/// <see cref="JsonSerializerOptions"/> (that's the established pattern for UserSettings);
/// this context provides a source-gen path for code that cannot use reflection — e.g. the
/// JustPlay.Core layer in a NativeAOT build, and the test suite.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(StreamServerProfile))]
[JsonSerializable(typeof(List<StreamServerProfile>))]
[JsonSerializable(typeof(StreamFormat))]
[JsonSerializable(typeof(IcecastProtocol))]
public sealed partial class StreamingJsonContext : JsonSerializerContext;
