using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace JustPlay.Core.Audio;

/// <summary>One audio port of a PipeWire node, as <c>pw-dump</c> describes it.</summary>
/// <param name="Id">The port's global object id - what <c>pw-link</c> takes.</param>
/// <param name="Index">The port's index inside its node (<c>port.id</c>) - channel order.</param>
/// <param name="Channel">The channel position (<c>audio.channel</c>: FL, FR, AUX0, MONO, ...); empty when unset.</param>
public readonly record struct PipeWirePort(int Id, int Index, string Channel);

/// <summary>An app's playback stream (a <c>Stream/Output/Audio</c> node) with its output ports in channel order.</summary>
public sealed record PipeWireAppStream(int NodeId, int ProcessId, string Binary, IReadOnlyList<PipeWirePort> Ports);

/// <summary>
/// The Linux "capture a specific APP" brain, kept free of process spawning so it is unit-testable:
/// reads a <c>pw-dump</c> snapshot (PipeWire's whole object graph as JSON) and decides which of an
/// app's output ports to link into the two input ports of our recorder. The adapter that runs the
/// PipeWire tools lives in JustPlay.Audio.Bass (<c>PipeWireProcessAudioCapture</c>).
///
/// <para>Linking the ports ourselves - instead of letting the session manager connect the recorder
/// to one target - is what makes the Master-pair choice possible (the DJ cue-bleed fix, see
/// <see cref="AppCaptureFormat"/>) and lets a browser's re-created stream be picked up again.
/// PipeWire sums every link that arrives at one input port, so several streams of one app (or
/// FullMix's several channels) mix by themselves.</para>
/// </summary>
public static class PipeWireGraph
{
    /// <summary>
    /// Every app playback stream in the snapshot. The process id and binary come from the node's
    /// own properties when the client put them there (pipewire-pulse does), else from the client
    /// object it belongs to (native PipeWire clients only describe themselves there). Streams with
    /// no known process are dropped - they cannot be offered in a per-process picker.
    /// </summary>
    public static IReadOnlyList<PipeWireAppStream> ParseAppStreams(string pwDumpJson)
    {
        using var doc = JsonDocument.Parse(pwDumpJson);
        var clients = new Dictionary<int, JsonElement>();
        var nodes = new List<(int Id, JsonElement Props)>();
        var ports = new Dictionary<int, List<PipeWirePort>>();

        foreach (var obj in doc.RootElement.EnumerateArray())
        {
            if (!obj.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
            if (!TryGetProps(obj, out var props)) continue;

            switch (Str(obj, "type"))
            {
                case "PipeWire:Interface:Client":
                    clients[id] = props;
                    break;
                case "PipeWire:Interface:Node":
                    var cls = Str(props, "media.class");
                    if (cls is "Stream/Output/Audio" or "Stream/Duplex/Audio")
                        nodes.Add((id, props));
                    break;
                case "PipeWire:Interface:Port":
                    if (Str(props, "port.direction") != "out" || Bool(props, "port.monitor")) break;
                    if (Int(props, "node.id") is not { } nodeId) break;
                    if (!ports.TryGetValue(nodeId, out var list)) ports[nodeId] = list = [];
                    list.Add(new PipeWirePort(id, Int(props, "port.id") ?? list.Count, Str(props, "audio.channel")));
                    break;
            }
        }

        var streams = new List<PipeWireAppStream>();
        foreach (var (id, props) in nodes)
        {
            JsonElement? client = Int(props, "client.id") is { } cid && clients.TryGetValue(cid, out var c) ? c : null;
            var pid = Int(props, "application.process.id")
                      ?? (client is { } cp ? Int(cp, "application.process.id") : null);
            if (pid is not > 0) continue;

            var binary = First(Str(props, "application.process.binary"),
                               client is { } cb ? Str(cb, "application.process.binary") : "",
                               Str(props, "application.name"),
                               client is { } cn ? Str(cn, "application.name") : "");
            var nodePorts = ports.TryGetValue(id, out var p) ? p.OrderBy(x => x.Index).ToList() : [];
            streams.Add(new PipeWireAppStream(id, pid.Value, binary, nodePorts));
        }
        return streams;
    }

    /// <summary>
    /// The input ports of the node named <paramref name="nodeName"/> (our recorder), in channel
    /// order. Empty until the recorder has registered with PipeWire.
    /// </summary>
    public static IReadOnlyList<PipeWirePort> ParseInputPorts(string pwDumpJson, string nodeName)
    {
        using var doc = JsonDocument.Parse(pwDumpJson);
        int? nodeId = null;
        var all = new List<(int NodeId, PipeWirePort Port)>();

        foreach (var obj in doc.RootElement.EnumerateArray())
        {
            if (!obj.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
            if (!TryGetProps(obj, out var props)) continue;
            var type = Str(obj, "type");
            if (type == "PipeWire:Interface:Node" && Str(props, "node.name") == nodeName)
                nodeId = id;
            else if (type == "PipeWire:Interface:Port" && Str(props, "port.direction") == "in"
                     && Int(props, "node.id") is { } owner)
                all.Add((owner, new PipeWirePort(id, Int(props, "port.id") ?? 0, Str(props, "audio.channel"))));
        }

        return nodeId is { } n
            ? all.Where(x => x.NodeId == n).Select(x => x.Port).OrderBy(x => x.Index).ToList()
            : [];
    }

    /// <summary>
    /// The streams that belong to the app picked as <paramref name="processId"/>: its own, plus
    /// those of other processes running the same binary. The picker collapses an app to its lowest
    /// pid (<see cref="CaptureAppFilter"/>), but the audio may come from a sibling process (a
    /// browser's audio service) - Windows' process loopback covers the process tree the same way.
    /// </summary>
    public static IReadOnlyList<PipeWireAppStream> StreamsOf(IReadOnlyList<PipeWireAppStream> streams, int processId)
    {
        var binary = streams.FirstOrDefault(s => s.ProcessId == processId)?.Binary;
        return streams
            .Where(s => s.ProcessId == processId
                        || (!string.IsNullOrEmpty(binary) && string.Equals(s.Binary, binary, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// Which of a stream's output ports feed our LEFT and RIGHT input. A Master-pair format takes
    /// exactly the pair at <see cref="AppCaptureFormat.MasterChannelOffset"/> (a stream with fewer
    /// channels degrades to its first pair, as the macOS tap does); FullMix folds every channel onto
    /// its side, centres onto both, and leaves the LFE out - a stereo fold-down, which on a
    /// multi-out DJ device mixes Master and Cue together, exactly as documented for FullMix.
    /// </summary>
    public static IReadOnlyList<(int OutPortId, bool ToLeft)> PlanLinks(IReadOnlyList<PipeWirePort> ports, AppCaptureFormat format)
    {
        var plan = new List<(int, bool)>();
        if (ports.Count == 0) return plan;
        if (ports.Count == 1)
        {
            plan.Add((ports[0].Id, true));
            plan.Add((ports[0].Id, false));
            return plan;
        }

        if (format.CaptureChannels > 2)
        {
            var offset = format.MasterChannelOffset + 1 < ports.Count ? format.MasterChannelOffset : 0;
            plan.Add((ports[offset].Id, true));
            plan.Add((ports[offset + 1].Id, false));
            return plan;
        }

        for (var i = 0; i < ports.Count; i++)
        {
            var ch = ports[i].Channel;
            if (LeftChannels.Contains(ch)) plan.Add((ports[i].Id, true));
            else if (RightChannels.Contains(ch)) plan.Add((ports[i].Id, false));
            else if (ch is "LFE" or "LFE2") { /* no low-frequency effects channel in a stereo fold-down */ }
            else if (ch.Length == 0 || ch.StartsWith("AUX", StringComparison.Ordinal) || ch == "UNK")
                plan.Add((ports[i].Id, i % 2 == 0)); // unnamed positions: interleave by order, like a plain multi-out
            else
            {
                plan.Add((ports[i].Id, true));   // a centre (FC, RC, MONO, ...) belongs to both sides
                plan.Add((ports[i].Id, false));
            }
        }
        return plan;
    }

    // SPA channel positions by side. "R" also means REAR (RL = rear left), so a letter test cannot work.
    private static readonly HashSet<string> LeftChannels =
        ["FL", "SL", "RL", "FLC", "RLC", "TFL", "TRL", "TSL", "FLW", "FLH", "TFLC", "BLC", "LLFE"];
    private static readonly HashSet<string> RightChannels =
        ["FR", "SR", "RR", "FRC", "RRC", "TFR", "TRR", "TSR", "FRW", "FRH", "TFRC", "BRC", "RLFE"];

    // -- pw-dump JSON helpers: props values are numbers OR strings depending on the client --------

    private static bool TryGetProps(JsonElement obj, out JsonElement props)
    {
        props = default;
        return obj.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
               && info.TryGetProperty("props", out props) && props.ValueKind == JsonValueKind.Object;
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";

    private static int? Int(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
        return null;
    }

    private static bool Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

    private static string First(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
