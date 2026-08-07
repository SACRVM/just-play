using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Core.Streaming;

/// <summary>
/// Reads a mount's listener stats from Icecast's public status page.
///
/// <para>Verified against a real, standard server on 2026-08-07 (Icecast 2.4.0-kh22 behind
/// AzuraCast): <c>/status-json.xsl</c> answers 200 over both HTTP and HTTPS with no credentials at
/// all, and carries listeners, the peak, the source bitrate and the current title. The admin
/// endpoint (<c>/admin/stats</c>) would need a SECOND password we do not store and do not want to -
/// the public page is enough for what the UI shows.</para>
///
/// <para>Parsing goes through <see cref="JsonDocument"/> rather than a deserialised model: it is
/// reflection-free (so trim/AOT-safe without a source-generated context) and, more to the point, it
/// copes with the shape below without contortions.</para>
/// </summary>
public sealed class IcecastStatsProbe(HttpClient http) : IStreamStatsProbe
{
    /// <summary>The status page every Icecast 2.4+ serves. Older servers answer 404 and the caller
    /// simply gets null.</summary>
    public const string StatusPath = "/status-json.xsl";

    public async Task<StreamStats?> TryReadAsync(
        StreamServerProfile server, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var scheme = server.UseTls ? "https" : "http";
        var url = $"{scheme}://{server.Host}:{server.Port}{StatusPath}";

        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, server.Mount);
        }
        catch (Exception)
        {
            // An unreachable server, a hidden status page, a captive-portal HTML page where JSON was
            // expected: all of them mean "we do not know", and not knowing is a dash on screen. A
            // stats poll must never be able to interrupt a broadcast.
            return null;
        }
    }

    /// <summary>
    /// Pulls the stats for <paramref name="mount"/> out of a status-json payload. Public and static
    /// because this - not the HTTP - is the part that can be wrong, so it is what the tests drive.
    /// </summary>
    public static StreamStats? Parse(string json, string mount)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("icestats", out var stats)) return null;

            var serverId = Str(stats, "server_id");

            // (!) THE TRAP: "source" is an OBJECT when the server has ONE mount and an ARRAY when it
            // has several. A parser that assumes either one breaks on the other, and a station tends
            // to have exactly one mount right up until it does not.
            if (!stats.TryGetProperty("source", out var source)) return null;

            if (source.ValueKind == JsonValueKind.Object)
                return From(source, serverId);

            if (source.ValueKind != JsonValueKind.Array) return null;

            JsonElement? fallback = null;
            foreach (var candidate in source.EnumerateArray())
            {
                if (Matches(candidate, mount)) return From(candidate, serverId);
                fallback ??= candidate;
            }

            // Several mounts and none of them matched: rather than claim numbers that belong to
            // another stream, say nothing. A wrong listener count is worse than no listener count.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Is this entry our mount? The payload has no single reliable "mount" field across builds, so
    /// this checks the ones that do appear: an explicit mount, the listen URL's tail, and the mount
    /// name without its leading slash (which is what <c>server_name</c> often holds).
    /// </summary>
    private static bool Matches(JsonElement source, string mount)
    {
        if (string.IsNullOrWhiteSpace(mount)) return false;
        var bare = mount.TrimStart('/');

        if (Str(source, "mount") is { } m &&
            m.TrimStart('/').Equals(bare, StringComparison.OrdinalIgnoreCase)) return true;

        if (Str(source, "listenurl") is { } url &&
            url.EndsWith(mount, StringComparison.OrdinalIgnoreCase)) return true;

        return Str(source, "server_name") is { } name
            && name.Equals(bare, StringComparison.OrdinalIgnoreCase);
    }

    private static StreamStats From(JsonElement source, string? serverId) => new(
        Listeners: Int(source, "listeners") ?? 0,
        PeakListeners: Int(source, "listener_peak") ?? 0,
        Title: Str(source, "title"),
        BitrateKbps: Int(source, "bitrate"),
        ServerId: serverId);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>Some builds quote their numbers, so accept a numeric string too.</summary>
    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : null,
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : null,
            _ => null,
        };
    }
}
