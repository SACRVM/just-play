using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Audio;
using JustPlay.Core.Models;

namespace JustPlay.Audio.Bass;

/// <summary>
/// macOS implementation of <see cref="IProcessAudioCapture"/> via Core Audio process taps
/// (macOS 14.4+), through our native shim <c>libjuststream_capture.dylib</c>
/// (native/osx-src/juststream_capture.m - flat C ABI; see research/macos-app-audio-capture.md).
///
/// The shim taps ONE process at the rate of the device the target renders to; this class
/// linearly resamples to the requested rate when they differ (fine for 44.1<->48 on a lossy-
/// encoded broadcast; swap for a proper SRC if it ever matters). Channel handling mirrors
/// Windows (<see cref="AppCaptureFormat"/>): FullMix taps a stereo MIXDOWN of everything the
/// app plays; Master12/34 tap the app's output DEVICE stream with its full channel layout and
/// extract one pair - the DJ cue-bleed fix (a mixdown mixes Master + Cue together; verified
/// live with Traktor on a 4-out device, 2026-07-15).
///
/// TCC: the FIRST capture start triggers macOS's "System Audio Recording Only" prompt - the
/// hosting .app must carry NSAudioCaptureUsageDescription and be code-signed, or the OS
/// delivers SILENCE with no prompt (verified 2026-07-15 with an unblessed host: buffers flow,
/// all zeros). Same silent-fail family as the microphone key.
/// </summary>
public sealed class MacProcessAudioCapture : IProcessAudioCapture
{
    private const string Lib = "juststream_capture"; // -> libjuststream_capture.dylib beside the app

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ProcessInfo
    {
        public int Pid;
        public int IsPlaying;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string BundleId;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioCallback(IntPtr pcm, int frames, int channels, double sampleRate, IntPtr user);

    [DllImport(Lib)] private static extern int jsc_is_supported();
    [DllImport(Lib)] private static extern int jsc_list_processes([Out] ProcessInfo[] infos, int cap);
    [DllImport(Lib)] private static extern int jsc_start_tap(int pid, int mute, int fullChannels, AudioCallback cb, IntPtr user);
    [DllImport(Lib)] private static extern void jsc_stop_tap();

    // Pinned for the lifetime of this instance - the shim holds the raw function pointer
    // (same rule as the BASS sync delegates, see BassAudioEngine._endSync / CLAUDE.md).
    private readonly AudioCallback _onAudio;

    private float[] _out = new float[16384];
    private int _targetRate;
    private int _chanOffset;  // interleaved index of the broadcast pair (0 = ch 1/2, 2 = ch 3/4)
    private double _srcPhase; // fractional source-frame position carried across callbacks
    private float _lastL, _lastR;

    public MacProcessAudioCapture()
    {
        _onAudio = OnAudio;
        IsSupported = OperatingSystem.IsMacOSVersionAtLeast(14, 4) && Probe();
    }

    private static bool Probe()
    {
        try { return jsc_is_supported() == 1; }
        catch { return false; } // dylib missing/unloadable -> unsupported, never crash DI
    }

    public bool IsSupported { get; }

    public bool IsCapturing { get; private set; }

    public event Action<float[], int>? FramesAvailable;

    public IReadOnlyList<CaptureApp> GetCapturableApps()
    {
        if (!IsSupported) return Array.Empty<CaptureApp>();
        var raw = new ProcessInfo[512];
        int n;
        try { n = jsc_list_processes(raw, raw.Length); }
        catch { return Array.Empty<CaptureApp>(); }
        if (n <= 0) return Array.Empty<CaptureApp>();

        var procs = new List<RunningProcess>(n);
        var self = Environment.ProcessId;
        for (var i = 0; i < n; i++)
        {
            ref readonly var p = ref raw[i];
            if (p.Pid == self || string.IsNullOrWhiteSpace(p.Name)) continue;
            // "Has a main window" doesn't exist as a cheap concept here; the useful analogue for
            // the shared filter: a NON-Apple bundled app (drops coreaudiod helpers/daemons) or a
            // process that is audibly playing right now.
            var windowed = (!string.IsNullOrEmpty(p.BundleId)
                            && !p.BundleId.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase))
                           || p.IsPlaying != 0;
            procs.Add(new RunningProcess(p.Pid, p.Name, windowed));
        }
        return CaptureAppFilter.ToCaptureApps(procs);
    }

    public void Start(int processId, int sampleRate, AppCaptureFormat format)
    {
        if (!IsSupported)
            throw new NotSupportedException("Per-app capture needs macOS 14.4 or newer.");
        // Channel handling mirrors Windows: FullMix (CaptureChannels == 2) taps a stereo
        // MIXDOWN of everything the app plays; Master12/34 tap the app's output DEVICE with
        // its full channel layout and extract one pair - so a DJ app's headphone Cue on the
        // other pair stays out of the broadcast instead of bleeding in.
        var full = format.CaptureChannels > 2;
        _chanOffset = full ? format.MasterChannelOffset : 0;
        _targetRate = sampleRate;
        _srcPhase = 0; _lastL = 0; _lastR = 0;

        var rc = jsc_start_tap(processId, mute: 0, full ? 1 : 0, _onAudio, IntPtr.Zero);
        if (rc != 0)
        {
            IsCapturing = false;
            throw new NotSupportedException(rc switch
            {
                -1 => $"Process {processId} is not known to Core Audio (already exited?).",
                -2 => "Creating the process tap failed.",
                -3 => "Creating the tap aggregate device failed.",
                -4 => "Starting the tap IO failed.",
                -5 => "Core Audio process taps are unavailable on this macOS.",
                _ => $"Process tap failed ({rc}).",
            });
        }
        IsCapturing = true;
    }

    public void Stop()
    {
        if (!IsCapturing) return;
        try { jsc_stop_tap(); } catch { /* never let native teardown throw */ }
        IsCapturing = false;
    }

    public void Dispose() => Stop();

    // -- capture thread (Core Audio IOProc) -----------------------------------

    private unsafe void OnAudio(IntPtr pcm, int frames, int channels, double sampleRate, IntPtr user)
    {
        var handler = FramesAvailable;
        if (handler is null || frames <= 0 || channels <= 0) return;
        var src = (float*)pcm;

        // The broadcast pair inside the interleaved source frame. A tap with fewer channels
        // than the requested offset (app moved to a stereo device mid-capture) degrades to
        // the first pair instead of reading out of bounds.
        var lIdx = _chanOffset + 1 < channels ? _chanOffset : 0;
        var rIdx = channels > 1 ? lIdx + 1 : lIdx;

        int count;
        if ((int)sampleRate == _targetRate && channels == 2 && lIdx == 0)
        {
            // Fast path: rate matches, already the wanted stereo pair - hand through as-is.
            count = frames * 2;
            if (_out.Length < count) _out = new float[count];
            new ReadOnlySpan<float>(src, count).CopyTo(_out);
            _lastL = src[(frames - 1) * 2];
            _lastR = src[(frames - 1) * 2 + 1];
        }
        else if ((int)sampleRate == _targetRate)
        {
            // Rate matches, multi-channel source: plain pair extraction.
            count = frames * 2;
            if (_out.Length < count) _out = new float[count];
            for (var f = 0; f < frames; f++)
            {
                _out[f * 2]     = src[f * channels + lIdx];
                _out[f * 2 + 1] = src[f * channels + rIdx];
            }
            _lastL = src[(frames - 1) * channels + lIdx];
            _lastR = src[(frames - 1) * channels + rIdx];
        }
        else
        {
            // Linear resample of the extracted pair. Phase carries across callbacks; index -1
            // interpolates from the previous block's last frame so block joints stay click-free.
            var step = sampleRate / _targetRate;
            var max = (int)(frames / step) * 2 + 8;
            if (_out.Length < max) _out = new float[max];
            count = 0;
            var pos = _srcPhase;
            while (true)
            {
                var i0 = (int)Math.Floor(pos);
                var i1 = i0 + 1;
                if (i1 >= frames) break;
                var frac = (float)(pos - i0);
                float l0 = i0 < 0 ? _lastL : src[i0 * channels + lIdx];
                float r0 = i0 < 0 ? _lastR : src[i0 * channels + rIdx];
                float l1 = src[i1 * channels + lIdx];
                float r1 = src[i1 * channels + rIdx];
                _out[count++] = l0 + (l1 - l0) * frac;
                _out[count++] = r0 + (r1 - r0) * frac;
                pos += step;
            }
            _srcPhase = pos - frames;
            _lastL = src[(frames - 1) * channels + lIdx];
            _lastR = src[(frames - 1) * channels + rIdx];
        }

        if (count > 0) handler(_out, count);
    }
}
