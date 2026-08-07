using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JustPlay.Core.Updates;

/// <summary>
/// Reads the repo's latest GitHub Release (<c>GET /repos/{owner}/{repo}/releases/latest</c>),
/// parses the tag into a version, and reports it when it beats the running one. Draft and
/// pre-release entries are skipped (the "latest" endpoint already excludes them, but we
/// double-check). The <c>JustPlaySetup-*.exe</c> asset, if present, is surfaced so the shell
/// can download + run it silently.
/// <para>
/// The <see cref="HttpClient"/> is injected so tests drive it with a fake handler - we don't
/// mock the network, we mock the transport boundary. Platform-agnostic: BCL only.
/// </para>
/// </summary>
public sealed class GitHubUpdateChecker : IUpdateChecker
{
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly Action<string>? _log;

    public GitHubUpdateChecker(HttpClient http, string owner, string repo, Action<string>? log = null)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
        _log = log;
    }

    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            // GitHub rejects API requests without a User-Agent. Set it per-request so a shared
            // HttpClient without default headers still works.
            if (!req.Headers.Contains("User-Agent"))
            {
                req.Headers.UserAgent.ParseAdd("JustPlay-Updater");
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"latest-release HTTP {(int)resp.StatusCode}");
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Parse(doc.RootElement, Normalize(current), _owner, _repo, _log);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _log?.Invoke($"check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parse a GitHub release JSON object. Internal for unit testing.</summary>
    internal static UpdateInfo? Parse(JsonElement root, Version current, string owner, string repo, Action<string>? log)
    {
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(tag)) return null;
        if (!TryParseTag(tag, out var remote))
        {
            log?.Invoke($"unparseable tag '{tag}'");
            return null;
        }
        if (remote <= current) return null;

        var html = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
        var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

        string? assetUrl = null;
        string? assetName = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var an = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (an is null) continue;
                if (an.StartsWith("JustPlaySetup", StringComparison.OrdinalIgnoreCase)
                    && an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    assetName = an;
                    assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    assetSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    break;
                }
            }
        }

        return new UpdateInfo(
            remote,
            tag!,
            html ?? $"https://github.com/{owner}/{repo}/releases",
            body,
            assetUrl,
            assetName,
            assetSize);
    }

    /// <summary>
    /// Parse a release tag (e.g. <c>v0.2.0</c>, <c>0.2.0</c>, <c>v1.0.0-beta</c>) into a
    /// normalised Major.Minor.Build version. Any pre-release / build suffix after the first
    /// '-' or '+' is dropped. Internal for unit testing.
    /// </summary>
    internal static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return false;
        version = Normalize(v);
        return true;
    }

    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
