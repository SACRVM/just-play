using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Updates;
using Xunit;

namespace JustPlay.Core.Tests;

public class GitHubUpdateCheckerTests
{
    [Theory]
    [InlineData("v0.5.23", 0, 5, 23)]
    [InlineData("0.5.23", 0, 5, 23)]
    [InlineData("V1.0.0", 1, 0, 0)]
    [InlineData("v1.2.0-beta.1", 1, 2, 0)]   // pre-release suffix dropped
    [InlineData("v2.0+build7", 2, 0, 0)]      // build metadata dropped, build comp 0
    public void TryParseTag_parses_and_normalises(string tag, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateChecker.TryParseTag(tag, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("v")]
    [InlineData("")]
    public void TryParseTag_rejects_garbage(string tag)
    {
        Assert.False(GitHubUpdateChecker.TryParseTag(tag, out _));
    }

    [Fact]
    public void Parse_returns_null_when_remote_not_newer()
    {
        var json = Release("v0.5.22");
        var result = GitHubUpdateChecker.Parse(json.RootElement, new Version(0, 5, 22), "o", "r", null);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_for_prerelease()
    {
        var json = Release("v9.9.9", prerelease: true);
        var result = GitHubUpdateChecker.Parse(json.RootElement, new Version(0, 5, 22), "o", "r", null);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_surfaces_installer_asset_when_newer()
    {
        var json = Release("v0.5.23",
            assetName: "JustPlaySetup-0.5.23-win-x64.exe",
            assetUrl: "https://example/download/setup.exe",
            assetSize: 12345,
            body: "Notes here");
        var result = GitHubUpdateChecker.Parse(json.RootElement, new Version(0, 5, 22), "o", "r", null);

        Assert.NotNull(result);
        Assert.Equal(new Version(0, 5, 23), result!.Version);
        Assert.Equal("JustPlaySetup-0.5.23-win-x64.exe", result.InstallerAssetName);
        Assert.Equal("https://example/download/setup.exe", result.InstallerAssetUrl);
        Assert.Equal(12345, result.InstallerAssetSize);
        Assert.Equal("Notes here", result.ReleaseNotes);
    }

    [Fact]
    public void Parse_tolerates_release_without_installer_asset()
    {
        var json = Release("v0.5.23", assetName: "some-other.zip", assetUrl: "https://example/x.zip");
        var result = GitHubUpdateChecker.Parse(json.RootElement, new Version(0, 5, 22), "o", "r", null);

        Assert.NotNull(result);
        Assert.Null(result!.InstallerAssetUrl);
    }

    [Fact]
    public async Task CheckAsync_returns_info_for_newer_release()
    {
        var handler = new StubHandler(HttpStatusCode.OK, RawRelease("v0.5.23",
            assetName: "JustPlaySetup-0.5.23-win-x64.exe", assetUrl: "https://example/s.exe", assetSize: 9));
        var checker = new GitHubUpdateChecker(new HttpClient(handler), "o", "r");

        var info = await checker.CheckAsync(new Version(0, 5, 22), CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(new Version(0, 5, 23), info!.Version);
    }

    [Fact]
    public async Task CheckAsync_returns_null_on_http_error()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "{}");
        var checker = new GitHubUpdateChecker(new HttpClient(handler), "o", "r");

        var info = await checker.CheckAsync(new Version(0, 5, 22), CancellationToken.None);

        Assert.Null(info);
    }

    private static JsonDocument Release(
        string tag,
        bool prerelease = false,
        string? assetName = null,
        string? assetUrl = null,
        long assetSize = 0,
        string? body = null)
        => JsonDocument.Parse(RawRelease(tag, prerelease, assetName, assetUrl, assetSize, body));

    private static string RawRelease(
        string tag,
        bool prerelease = false,
        string? assetName = null,
        string? assetUrl = null,
        long assetSize = 0,
        string? body = null)
    {
        var assets = assetName is null
            ? "[]"
            : $$"""[{"name":"{{assetName}}","browser_download_url":"{{assetUrl}}","size":{{assetSize}}}]""";
        return $$"""
        {
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/o/r/releases/tag/{{tag}}",
          "body": {{JsonSerializer.Serialize(body)}},
          "assets": {{assets}}
        }
        """;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }
}
