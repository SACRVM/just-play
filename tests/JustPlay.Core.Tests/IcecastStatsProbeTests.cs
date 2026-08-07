using JustPlay.Core.Streaming;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Parsing Icecast's public status page. The payloads below are the SHAPES a real server sends - the
/// single-mount one is a live capture from a standard Icecast 2.4.0-kh22 (2026-08-07), trimmed and
/// with the host neutralised.
/// </summary>
public class IcecastStatsProbeTests
{
    /// <summary>A real answer, one mount. Note "source" is an OBJECT here.</summary>
    private const string OneMount = """
        {"icestats":{"admin":"icemaster@localhost","host":"stream.example.com",
         "location":"AzuraCast","server_id":"Icecast 2.4.0-kh22",
         "source":{"bitrate":320,"connected":5,"genre":"Mixed","listener_peak":12,"listeners":7,
                   "listenurl":"https://stream.example.com/listen/chloe/chloe",
                   "server_name":"chloe","server_type":"audio/mpeg",
                   "title":"Streaming live from Chrome"}}}
        """;

    /// <summary>The SAME server with a second mount - "source" is an ARRAY now.</summary>
    private const string TwoMounts = """
        {"icestats":{"server_id":"Icecast 2.4.0-kh22","source":[
          {"listeners":3,"listener_peak":4,"server_name":"other","listenurl":"http://x/other","title":"Other"},
          {"listeners":7,"listener_peak":12,"server_name":"chloe","listenurl":"http://x/chloe","title":"Mine","bitrate":320}
        ]}}
        """;

    [Fact]
    public void Reads_a_single_mount_server()
    {
        var stats = IcecastStatsProbe.Parse(OneMount, "/chloe");

        Assert.NotNull(stats);
        Assert.Equal(7, stats!.Listeners);
        Assert.Equal(12, stats.PeakListeners);
        Assert.Equal(320, stats.BitrateKbps);
        Assert.Equal("Streaming live from Chrome", stats.Title);
        Assert.Equal("Icecast 2.4.0-kh22", stats.ServerId);
    }

    [Fact]
    public void A_single_mount_is_taken_whatever_it_is_called()
    {
        // With one source there is nothing to confuse it with, so the mount name must not gate it -
        // the profile's mount and the server's idea of it disagree more often than you would think
        // (here the profile says "/chloe" and listenurl is ".../listen/chloe/chloe").
        Assert.NotNull(IcecastStatsProbe.Parse(OneMount, "/something-else"));
    }

    [Fact]
    public void Picks_the_right_mount_out_of_several()
    {
        var stats = IcecastStatsProbe.Parse(TwoMounts, "/chloe");

        Assert.Equal(7, stats!.Listeners);
        Assert.Equal("Mine", stats.Title);
    }

    [Fact]
    public void Says_nothing_rather_than_the_wrong_mounts_numbers()
    {
        // Several mounts, none of them ours. Reporting the first one would put another stream's
        // audience on our screen - a wrong number is worse than no number.
        Assert.Null(IcecastStatsProbe.Parse(TwoMounts, "/not-here"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>404 Not Found</html>")]           // status page hidden -> HTML, not JSON
    [InlineData("{\"icestats\":{}}")]                    // server up, no source connected
    [InlineData("{\"something\":\"else\"}")]
    public void Anything_that_is_not_a_usable_answer_is_null(string payload)
    {
        // A stats poll must never throw: an unknown count is a dash on screen, not a crash mid-set.
        Assert.Null(IcecastStatsProbe.Parse(payload, "/chloe"));
    }

    [Fact]
    public void Quoted_numbers_are_accepted()
    {
        const string quoted =
            """{"icestats":{"source":{"listeners":"5","listener_peak":"9","server_name":"chloe"}}}""";

        var stats = IcecastStatsProbe.Parse(quoted, "/chloe");

        Assert.Equal(5, stats!.Listeners);
        Assert.Equal(9, stats.PeakListeners);
    }

    [Fact]
    public void A_missing_count_reads_as_zero_not_as_a_crash()
    {
        const string sparse = """{"icestats":{"source":{"server_name":"chloe"}}}""";

        var stats = IcecastStatsProbe.Parse(sparse, "/chloe");

        Assert.NotNull(stats);
        Assert.Equal(0, stats!.Listeners);
        Assert.Null(stats.Title);
    }
}
