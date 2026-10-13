using System.Linq;
using JustPlay.Core.Audio;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// The Linux app-capture brain. The snapshot below is a trimmed pw-dump (PipeWire 1.6.9) with the
/// shapes that occur in the wild: a pipewire-pulse client that puts its process on the NODE
/// (Firefox), a native client that only describes itself on the CLIENT object, a 4-channel DJ
/// stream, a sink with monitor ports, and our own recorder.
/// </summary>
public class PipeWireGraphTests
{
    private const string Dump = """
    [
      { "id": 53, "type": "PipeWire:Interface:Node",
        "info": { "props": { "media.class": "Audio/Sink", "node.name": "alsa_output.analog-stereo" } } },
      { "id": 60, "type": "PipeWire:Interface:Port",
        "info": { "direction": "output", "props": { "node.id": 53, "port.id": 0, "port.direction": "out", "port.monitor": true, "audio.channel": "FL" } } },

      { "id": 74, "type": "PipeWire:Interface:Client",
        "info": { "props": { "application.name": "Firefox", "application.process.binary": "firefox", "application.process.id": 786182 } } },
      { "id": 90, "type": "PipeWire:Interface:Node",
        "info": { "props": { "media.class": "Stream/Output/Audio", "application.name": "Firefox",
                             "application.process.binary": "firefox", "application.process.id": 786182, "client.id": 74 } } },
      { "id": 75, "type": "PipeWire:Interface:Port",
        "info": { "direction": "output", "props": { "node.id": 90, "port.id": 1, "port.direction": "out", "audio.channel": "FR" } } },
      { "id": 84, "type": "PipeWire:Interface:Port",
        "info": { "direction": "output", "props": { "node.id": 90, "port.id": 0, "port.direction": "out", "audio.channel": "FL" } } },

      { "id": 100, "type": "PipeWire:Interface:Client",
        "info": { "props": { "application.name": "Mixxx", "application.process.binary": "mixxx", "application.process.id": "4242" } } },
      { "id": 101, "type": "PipeWire:Interface:Node",
        "info": { "props": { "media.class": "Stream/Output/Audio", "node.name": "mixxx", "client.id": 100 } } },
      { "id": 102, "type": "PipeWire:Interface:Port", "info": { "direction": "output", "props": { "node.id": 101, "port.id": 0, "port.direction": "out", "audio.channel": "AUX0" } } },
      { "id": 103, "type": "PipeWire:Interface:Port", "info": { "direction": "output", "props": { "node.id": 101, "port.id": 1, "port.direction": "out", "audio.channel": "AUX1" } } },
      { "id": 104, "type": "PipeWire:Interface:Port", "info": { "direction": "output", "props": { "node.id": 101, "port.id": 2, "port.direction": "out", "audio.channel": "AUX2" } } },
      { "id": 105, "type": "PipeWire:Interface:Port", "info": { "direction": "output", "props": { "node.id": 101, "port.id": 3, "port.direction": "out", "audio.channel": "AUX3" } } },

      { "id": 110, "type": "PipeWire:Interface:Node",
        "info": { "props": { "media.class": "Stream/Output/Audio", "node.name": "orphan" } } },

      { "id": 81, "type": "PipeWire:Interface:Node",
        "info": { "props": { "media.class": "Stream/Input/Audio", "node.name": "juststream-capture-1" } } },
      { "id": 91, "type": "PipeWire:Interface:Port", "info": { "direction": "input", "props": { "node.id": 81, "port.id": 1, "port.direction": "in", "audio.channel": "FR" } } },
      { "id": 89, "type": "PipeWire:Interface:Port", "info": { "direction": "input", "props": { "node.id": 81, "port.id": 0, "port.direction": "in", "audio.channel": "FL" } } },

      { "id": 200, "type": "PipeWire:Interface:Link", "info": null }
    ]
    """;

    [Fact]
    public void App_streams_carry_their_process_whether_the_node_or_the_client_names_it()
    {
        var streams = PipeWireGraph.ParseAppStreams(Dump);

        var firefox = Assert.Single(streams, s => s.Binary == "firefox");
        Assert.Equal(786182, firefox.ProcessId);

        var mixxx = Assert.Single(streams, s => s.Binary == "mixxx");
        Assert.Equal(4242, mixxx.ProcessId); // a string-typed id on the client object
        Assert.Equal(101, mixxx.NodeId);
    }

    [Fact]
    public void A_stream_without_a_process_and_every_non_stream_node_are_left_out()
    {
        var streams = PipeWireGraph.ParseAppStreams(Dump);

        Assert.Equal(2, streams.Count);
        Assert.DoesNotContain(streams, s => s.NodeId is 110 or 53 or 81);
    }

    [Fact]
    public void Ports_come_in_channel_order_not_listing_order()
    {
        var firefox = PipeWireGraph.ParseAppStreams(Dump).Single(s => s.Binary == "firefox");

        Assert.Equal(new[] { "FL", "FR" }, firefox.Ports.Select(p => p.Channel));
        Assert.Equal(new[] { 84, 75 }, firefox.Ports.Select(p => p.Id));
    }

    [Fact]
    public void The_recorder_input_ports_are_found_by_node_name()
    {
        var inputs = PipeWireGraph.ParseInputPorts(Dump, "juststream-capture-1");

        Assert.Equal(new[] { 89, 91 }, inputs.Select(p => p.Id));
        Assert.Empty(PipeWireGraph.ParseInputPorts(Dump, "not-started-yet"));
    }

    [Fact]
    public void Sibling_processes_of_the_same_binary_belong_to_the_picked_app()
    {
        var streams = new[]
        {
            new PipeWireAppStream(1, 100, "firefox", []),
            new PipeWireAppStream(2, 117, "firefox", []),
            new PipeWireAppStream(3, 300, "mixxx", []),
        };

        Assert.Equal(new[] { 1, 2 }, PipeWireGraph.StreamsOf(streams, 100).Select(s => s.NodeId));
        Assert.Empty(PipeWireGraph.StreamsOf(streams, 999));
    }

    private static PipeWirePort[] Ports(params string[] channels) =>
        channels.Select((c, i) => new PipeWirePort(Id: 10 + i, Index: i, Channel: c)).ToArray();

    [Fact]
    public void Master_pair_on_a_four_channel_stream_takes_only_that_pair()
    {
        var ports = Ports("AUX0", "AUX1", "AUX2", "AUX3");

        Assert.Equal(new[] { (10, true), (11, false) }, PipeWireGraph.PlanLinks(ports, AppCaptureFormat.From(AppCaptureChannels.Master12)));
        Assert.Equal(new[] { (12, true), (13, false) }, PipeWireGraph.PlanLinks(ports, AppCaptureFormat.From(AppCaptureChannels.Master34)));
    }

    [Fact]
    public void Channels_3_4_on_a_stereo_stream_fall_back_to_the_only_pair()
    {
        var plan = PipeWireGraph.PlanLinks(Ports("FL", "FR"), AppCaptureFormat.From(AppCaptureChannels.Master34));

        Assert.Equal(new[] { (10, true), (11, false) }, plan);
    }

    [Fact]
    public void Full_mix_folds_a_surround_stream_by_side_and_drops_the_LFE()
    {
        // FL FR FC LFE RL RR - "R" in RL means REAR: it is a LEFT channel.
        var plan = PipeWireGraph.PlanLinks(Ports("FL", "FR", "FC", "LFE", "RL", "RR"), AppCaptureFormat.From(AppCaptureChannels.FullMix));

        Assert.Equal(new[] { 10, 12, 14 }, plan.Where(l => l.ToLeft).Select(l => l.OutPortId));
        Assert.Equal(new[] { 11, 12, 15 }, plan.Where(l => !l.ToLeft).Select(l => l.OutPortId));
        Assert.DoesNotContain(plan, l => l.OutPortId == 13);
    }

    [Fact]
    public void Full_mix_of_unnamed_channels_interleaves_by_order()
    {
        var plan = PipeWireGraph.PlanLinks(Ports("AUX0", "AUX1", "AUX2", "AUX3"), AppCaptureFormat.From(AppCaptureChannels.FullMix));

        Assert.Equal(new[] { (10, true), (11, false), (12, true), (13, false) }, plan);
    }

    [Fact]
    public void A_mono_stream_feeds_both_sides()
    {
        var plan = PipeWireGraph.PlanLinks(Ports("MONO"), AppCaptureFormat.From(AppCaptureChannels.Master12));

        Assert.Equal(new[] { (10, true), (10, false) }, plan);
    }
}
