using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Audio;
using JustPlay.Core.Models;

namespace JustPlay.Audio.Bass;

/// <summary>
/// Linux implementation of <see cref="IProcessAudioCapture"/> on PipeWire - the audio server of
/// every current desktop distribution, which routes each app's playback as its own stream node.
///
/// <para>How: a <c>pw-record</c> child process records 2-channel float PCM at the requested rate
/// to its stdout, started UNLINKED (<c>--target 0</c>); we then link the chosen app's output ports
/// into it with <c>pw-link</c>. Wiring it ourselves is what picks the Master pair on a multi-out DJ
/// stream (<see cref="PipeWireGraph.PlanLinks"/>), and PipeWire resamples to our rate on the way
/// in. Nothing is re-routed: the app keeps playing to its own device, we only add a listener.
/// Verified on PipeWire 1.6.9: a test source with swapped L/R links came back swapped, 44.1 kHz
/// resampled to 48 kHz, from a source that was not even connected to a speaker.</para>
///
/// <para>Why the PipeWire tools and not libpipewire: its API is callbacks over SPA pod structs, a
/// poor fit for P/Invoke, and the macOS route (a compiled C shim per platform) would add a native
/// build step to every Linux build. <c>pw-record</c>, <c>pw-link</c> and <c>pw-dump</c> ship with
/// PipeWire itself (Arch: pipewire, Debian/Ubuntu: pipewire-bin, Fedora: pipewire-utils).</para>
///
/// <para>An app only appears in PipeWire while it holds a playback stream open. DJ software keeps
/// one open the whole time it runs; a browser opens one when it starts playing and may drop it
/// on pause - so the picker lists apps that are (or were just) playing, and a timer re-links any
/// stream the captured app opens after the start.</para>
/// </summary>
public sealed class PipeWireProcessAudioCapture : IProcessAudioCapture
{
    private const int RelinkIntervalMs = 2000;
    private const int ReadFrames = 1024;

    private readonly object _gate = new();
    private readonly string _nodeName = $"juststream-capture-{Environment.ProcessId}";

    private Process? _recorder;
    private Thread? _reader;
    private Timer? _relinker;
    private int _processId;
    private AppCaptureFormat _format;
    private IReadOnlyList<PipeWirePort> _inputs = [];
    private readonly HashSet<int> _linkedNodes = [];
    private string _lastError = "";
    private int _relinking;

    public PipeWireProcessAudioCapture()
    {
        IsSupported = OperatingSystem.IsLinux() && Probe();
    }

    private static bool Probe()
    {
        try
        {
            if (!OnPath("pw-record") || !OnPath("pw-link")) return false;
            return Dump() is { } json && PipeWireGraph.ParseAppStreams(json) is not null;
        }
        catch { return false; } // no PipeWire server / tools -> unsupported, never crash DI
    }

    public bool IsSupported { get; }

    public bool IsCapturing { get; private set; }

    public event Action<float[], int>? FramesAvailable;

    public IReadOnlyList<CaptureApp> GetCapturableApps()
    {
        if (!IsSupported || Dump() is not { } json) return Array.Empty<CaptureApp>();
        var self = Environment.ProcessId;
        // Every stream is user-facing audio (the session's own sounds come from the desktop
        // shell, which the filter ranks last), so each counts as "windowed".
        var procs = PipeWireGraph.ParseAppStreams(json)
            .Where(s => s.ProcessId != self && s.Binary.Length > 0)
            .Select(s => new RunningProcess(s.ProcessId, s.Binary, HasMainWindow: true));
        return CaptureAppFilter.ToCaptureApps(procs);
    }

    public void Start(int processId, int sampleRate, AppCaptureFormat format)
    {
        if (!IsSupported)
            throw new NotSupportedException("Per-app capture needs PipeWire (pw-record / pw-link not found).");
        Stop();

        var json = Dump() ?? throw new NotSupportedException("PipeWire did not answer (pw-dump failed).");
        if (PipeWireGraph.StreamsOf(PipeWireGraph.ParseAppStreams(json), processId).Count == 0)
            throw new NotSupportedException(
                $"Process {processId} has no audio stream open right now - start playback in it, then pick it again.");

        var psi = new ProcessStartInfo("pw-record")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     "--target", "0", "--rate", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--channels", "2", "--channel-map", "FL,FR", "--format", "f32", "--raw", "--latency", "20ms",
                     "-P", $"{{ node.name={_nodeName} node.description=\"JUST STREAM capture\" media.role=Production }}",
                     "-",
                 })
            psi.ArgumentList.Add(a);

        var recorder = Process.Start(psi) ?? throw new NotSupportedException("pw-record did not start.");
        recorder.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _lastError = e.Data; };
        recorder.BeginErrorReadLine();

        lock (_gate)
        {
            _recorder = recorder;
            _processId = processId;
            _format = format;
            _linkedNodes.Clear();
            _reader = new Thread(() => ReadLoop(recorder)) { IsBackground = true, Name = "PipeWire capture" };
            _reader.Start();
        }

        // Our recorder's input ports exist once pw-record has registered its node - normally
        // within a few tens of milliseconds.
        var deadline = Environment.TickCount64 + 3000;
        IReadOnlyList<PipeWirePort> inputs = [];
        while (Environment.TickCount64 < deadline && !recorder.HasExited)
        {
            if (Dump() is { } now && (inputs = PipeWireGraph.ParseInputPorts(now, _nodeName)).Count >= 2) break;
            Thread.Sleep(30);
        }
        if (inputs.Count < 2)
        {
            var why = recorder.HasExited ? _lastError : "its input ports never appeared";
            Stop();
            throw new NotSupportedException($"pw-record failed to start: {why}");
        }

        lock (_gate) _inputs = inputs;
        if (Relink() == 0)
        {
            Stop();
            throw new NotSupportedException($"Could not link process {processId}'s audio (pw-link failed: {_lastError}).");
        }

        IsCapturing = true;
        _relinker = new Timer(_ => Relink(), null, RelinkIntervalMs, RelinkIntervalMs);
    }

    public void Stop()
    {
        Process? recorder; Thread? reader; Timer? relinker;
        lock (_gate)
        {
            recorder = _recorder; reader = _reader; relinker = _relinker;
            _recorder = null; _reader = null; _relinker = null;
            _inputs = [];
            _linkedNodes.Clear();
        }
        relinker?.Dispose();
        if (recorder is not null)
        {
            // Our links die with the recorder's ports - nothing else to undo in the graph.
            try { if (!recorder.HasExited) recorder.Kill(); } catch { /* already gone */ }
            reader?.Join(1000);
            recorder.Dispose();
        }
        IsCapturing = false;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Links every stream of the captured app that is not linked yet; returns how many streams are
    /// linked in total. Runs at start and then on a timer, so a stream the app re-creates (a browser
    /// resuming playback) is picked up again.
    /// </summary>
    private int Relink()
    {
        // A slow pw-dump must not let two timer ticks link the same stream twice.
        if (Interlocked.Exchange(ref _relinking, 1) == 1) return 0;
        try
        {
            IReadOnlyList<PipeWirePort> inputs; int pid; AppCaptureFormat format;
            lock (_gate)
            {
                if (_recorder is null || _inputs.Count < 2) return 0;
                inputs = _inputs; pid = _processId; format = _format;
            }
            if (Dump() is not { } json) return 0;

            var streams = PipeWireGraph.StreamsOf(PipeWireGraph.ParseAppStreams(json), pid);
            HashSet<int> linked;
            lock (_gate)
            {
                // Streams that went away free their node id; forget them so a re-used id links again.
                _linkedNodes.IntersectWith(streams.Select(s => s.NodeId));
                linked = [.. _linkedNodes];
            }

            foreach (var stream in streams.Where(s => !linked.Contains(s.NodeId)))
            {
                var ok = false;
                foreach (var (outPort, toLeft) in PipeWireGraph.PlanLinks(stream.Ports, format))
                    ok |= Link(outPort, toLeft ? inputs[0].Id : inputs[1].Id);
                if (ok) linked.Add(stream.NodeId);
            }

            lock (_gate)
            {
                if (_recorder is null) return 0; // stopped meanwhile - the links died with it
                _linkedNodes.UnionWith(linked);
                return _linkedNodes.Count;
            }
        }
        finally { Volatile.Write(ref _relinking, 0); }
    }

    // -- capture thread: raw interleaved f32 stereo from pw-record's stdout --------------------

    private void ReadLoop(Process recorder)
    {
        var bytes = new byte[ReadFrames * 2 * sizeof(float)];
        var floats = new float[ReadFrames * 2];
        var carry = 0;
        try
        {
            var stdout = recorder.StandardOutput.BaseStream;
            int n;
            while ((n = stdout.Read(bytes, carry, bytes.Length - carry)) > 0)
            {
                var total = carry + n;
                var whole = total / (2 * sizeof(float)) * (2 * sizeof(float)); // whole stereo frames only
                if (whole > 0)
                {
                    var count = whole / sizeof(float);
                    MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole)).CopyTo(floats);
                    FramesAvailable?.Invoke(floats, count);
                }
                carry = total - whole;
                if (carry > 0) Buffer.BlockCopy(bytes, whole, bytes, 0, carry);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The recorder was killed by Stop() - the pipe closing is the normal way out.
        }
    }

    // -- PipeWire tools ------------------------------------------------------------------------

    private bool Link(int outPort, int inPort)
    {
        var (exit, _, err) = Run("pw-link", outPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                 inPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (exit != 0 && !string.IsNullOrWhiteSpace(err)) _lastError = err.Trim();
        return exit == 0;
    }

    /// <summary>One <c>pw-dump</c> snapshot of the graph, or null when PipeWire is not reachable.</summary>
    private static string? Dump()
    {
        var (exit, output, _) = Run("pw-dump");
        return exit == 0 && output.Length > 0 ? output : null;
    }

    private static (int Exit, string Output, string Error) Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return (-1, "", $"{tool} did not start");
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { /* raced */ } return (-1, "", $"{tool} timed out"); }
        return (p.ExitCode, stdout, stderr.GetAwaiter().GetResult());
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, tool)));
}
