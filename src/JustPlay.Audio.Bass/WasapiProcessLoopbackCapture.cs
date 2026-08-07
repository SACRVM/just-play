using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Audio;
using JustPlay.Core.Models;

namespace JustPlay.Audio.Bass;

/// <summary>
/// Windows per-process audio capture via the WASAPI <b>Application Loopback</b> API (Win10 2004 /
/// build 19041+): <c>ActivateAudioInterfaceAsync</c> with
/// <c>AUDIOCLIENT_ACTIVATIONTYPE_PROCESS_LOOPBACK</c>. Captures ONE process's render stream
/// invisibly - the target app renders to its own device normally, unaware - with zero system-sound
/// leak, no virtual cable, no reconfiguration. This is the driverless Phase-0 "capture a specific
/// APP" source for JUST STREAM (see the just-route vision doc).
///
/// <para>The captured PCM is delivered as interleaved-stereo float via <see cref="FramesAvailable"/>;
/// <see cref="BassInputCaptureEngine"/> pushes it into the same mixer/DSP/limiter/encoder chain a
/// device source uses.</para>
///
/// <para>(!) Interop is classic <c>[ComImport]</c> (functional at runtime; NOT NativeAOT/trim-clean -
/// the eventual product would move this to source-generated CsWin32, per the just-check research). All
/// native calls are guarded: a failure surfaces a clear exception from <see cref="Start"/> and never
/// crashes the app.</para>
///
/// <para>Multi-out DJ hardware (Master + headphone Cue on one device) is handled: a stereo capture
/// downmixes everything (Cue leaks in), so for those the caller requests &gt; 2 channels and we capture
/// the native layout and broadcast ONLY the Master pair (<see cref="AppCaptureFormat"/>). Validated
/// 2026-07-02 on a Reloop Mixtour + Traktor: a 4-channel capture delivered Master (1/2) and Cue (3/4)
/// as SEPARATE channels (correlation ~ 0.18), so the Cue is dropped rather than mixed in.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiProcessLoopbackCapture : IProcessAudioCapture
{
    // -- Public API -------------------------------------------------------
    public event Action<float[], int>? FramesAvailable;

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    private volatile bool _capturing;
    public bool IsCapturing => _capturing;

    private Thread? _thread;
    private volatile bool _stop;
    private int _captureChannels = 2;   // channels requested from the source (2 = stereo downmix)
    private int _masterOffset;           // interleaved offset of the Master pair to broadcast

    public IReadOnlyList<CaptureApp> GetCapturableApps()
    {
        if (!IsSupported) return Array.Empty<CaptureApp>();
        var procs = new List<RunningProcess>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                bool hasWindow = false;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { /* access denied -> treat as no window */ }
                procs.Add(new RunningProcess(p.Id, p.ProcessName, hasWindow));
            }
            catch { /* process vanished / denied - skip */ }
            finally { p.Dispose(); }
        }
        return CaptureAppFilter.ToCaptureApps(procs);
    }

    public void Start(int processId, int sampleRate, AppCaptureFormat format)
    {
        if (!IsSupported)
            throw new NotSupportedException("WASAPI per-process loopback needs Windows 10 build 19041+ (2004).");
        Stop();

        _stop = false;
        _captureChannels = format.CaptureChannels < 2 ? 2 : format.CaptureChannels;
        _masterOffset = format.MasterChannelOffset;
        _thread = new Thread(() => CaptureLoop((uint)processId, sampleRate))
        {
            IsBackground = true,
            Name = "JustStream-AppCapture",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _capturing = true;
    }

    public void Stop()
    {
        _stop = true;
        var t = _thread;
        _thread = null;
        if (t != null && t.IsAlive && !ReferenceEquals(t, Thread.CurrentThread))
            t.Join(1000);
        _capturing = false;
    }

    public void Dispose() => Stop();

    // -- Capture thread ---------------------------------------------------

    private void CaptureLoop(uint processId, int sampleRate)
    {
        IntPtr eventHandle = IntPtr.Zero;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        try
        {
            client = ActivateProcessLoopbackClient(processId, sampleRate);

            eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
            if (eventHandle == IntPtr.Zero) throw new InvalidOperationException("CreateEvent failed.");
            client.SetEventHandle(eventHandle);

            var captureGuid = IID_IAudioCaptureClient;
            client.GetService(ref captureGuid, out var svc);
            capture = (IAudioCaptureClient)svc;

            client.Start();

            var raw = new float[sampleRate * _captureChannels]; // native-channel packets; grown on demand
            var stereo = new float[sampleRate * 2];              // extracted Master pair
            while (!_stop)
            {
                if (WaitForSingleObject(eventHandle, 200) != 0) continue; // timeout -> re-check _stop
                DrainPackets(capture, ref raw, ref stereo);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AppCapture] capture thread ended: {ex.Message}");
        }
        finally
        {
            try { client?.Stop(); } catch { }
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (eventHandle != IntPtr.Zero) CloseHandle(eventHandle);
            _capturing = false;
        }
    }

    private void DrainPackets(IAudioCaptureClient capture, ref float[] raw, ref float[] stereo)
    {
        capture.GetNextPacketSize(out uint packetFrames);
        while (packetFrames > 0 && !_stop)
        {
            capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
            int floats = checked((int)frames * _captureChannels);
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero && floats > 0)
            {
                if (raw.Length < floats) raw = new float[floats];
                Marshal.Copy(data, raw, 0, floats);
                if (_captureChannels == 2)
                {
                    FramesAvailable?.Invoke(raw, floats); // already stereo - deliver as-is
                }
                else
                {
                    // Multi-out source: broadcast ONLY the Master pair, dropping the headphone Cue (and
                    // any extra channels). Delivery stays interleaved-stereo so the mixer path is unchanged.
                    int n = ChannelExtractor.ToStereoPair(raw, floats, _captureChannels, _masterOffset, ref stereo);
                    if (n > 0) FramesAvailable?.Invoke(stereo, n);
                }
            }
            capture.ReleaseBuffer(frames);
            capture.GetNextPacketSize(out packetFrames);
        }
    }

    /// <summary>
    /// Run the async activation for a PROCESS_LOOPBACK IAudioClient and initialise it (shared,
    /// loopback + event callback, float32 stereo at the target rate). Blocks until activation
    /// completes. Throws on any failure.
    /// </summary>
    private IAudioClient ActivateProcessLoopbackClient(uint processId, int sampleRate)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = ProcessLoopback(1),
        //   ProcessLoopbackParams { TargetProcessId, ProcessLoopbackMode = IncludeTree(0) } }
        var acp = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATIONTYPE_PROCESS_LOOPBACK
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = processId,
                ProcessLoopbackMode = 0, // INCLUDE_TARGET_PROCESS_TREE
            },
        };
        IntPtr acpPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        IntPtr fmtPtr = IntPtr.Zero;
        var handler = new ActivationHandler();
        try
        {
            Marshal.StructureToPtr(acp, acpPtr, false);
            var prop = new PropVariantBlob
            {
                vt = VT_BLOB,
                cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = acpPtr,
            };
            Marshal.StructureToPtr(prop, propPtr, false);

            var iid = IID_IAudioClient;
            ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ref iid, propPtr, handler, out var op);

            if (!handler.Completed.Wait(3000))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete.");

            op.GetActivateResult(out int activateHr, out object clientObj);
            Marshal.ThrowExceptionForHR(activateHr);
            var client = (IAudioClient)clientObj;

            // Float32 at the requested rate; shared-mode, loopback + event-driven, auto-convert. Stereo
            // uses a plain WAVEFORMATEX (the proven path); >2 channels needs WAVEFORMATEXTENSIBLE with a
            // channel mask so the source's channels arrive SEPARATED (validated: Traktor 4ch -> Master
            // 1/2 and Cue 3/4 distinct, correlation ~ 0.18) and DrainPackets can isolate the Master pair.
            const uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                             | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            fmtPtr = BuildFormat(_captureChannels, sampleRate);
            try
            {
                client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000 /*200 ms in 100-ns*/, 0, fmtPtr, IntPtr.Zero);
            }
            catch when (_captureChannels > 2)
            {
                // A source that won't accept a multichannel format (rare) shouldn't kill the capture:
                // fall back to a plain stereo downmix (Master isolation is simply unavailable for it).
                Console.Error.WriteLine($"[AppCapture] {_captureChannels}-ch init failed; falling back to stereo.");
                Marshal.FreeHGlobal(fmtPtr);
                _captureChannels = 2;
                _masterOffset = 0;
                fmtPtr = BuildFormat(2, sampleRate);
                client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 2_000_000, 0, fmtPtr, IntPtr.Zero);
            }
            return client;
        }
        finally
        {
            if (fmtPtr != IntPtr.Zero) Marshal.FreeHGlobal(fmtPtr);
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(acpPtr);
        }
    }

    /// <summary>
    /// Allocate an unmanaged float capture format for <see cref="IAudioClient.Initialize"/>: a plain
    /// WAVEFORMATEX for stereo, or a WAVEFORMATEXTENSIBLE (with channel mask + IEEE-float sub-format)
    /// for &gt; 2 channels - which multichannel WASAPI requires. Caller frees the returned pointer.
    /// </summary>
    private static IntPtr BuildFormat(int channels, int sampleRate)
    {
        ushort block = (ushort)(channels * 32 / 8);
        if (channels <= 2)
        {
            var fmt = new WaveFormatEx
            {
                wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 32,
                nBlockAlign = block,
                nAvgBytesPerSec = (uint)(sampleRate * block),
                cbSize = 0,
            };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            Marshal.StructureToPtr(fmt, p, false);
            return p;
        }
        else
        {
            var fmt = new WaveFormatExtensible
            {
                wFormatTag = WAVE_FORMAT_EXTENSIBLE,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * block),
                nBlockAlign = block,
                wBitsPerSample = 32,
                cbSize = 22, // sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)
                wValidBitsPerSample = 32,
                dwChannelMask = ChannelMask(channels),
                subFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT,
            };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
            Marshal.StructureToPtr(fmt, p, false);
            return p;
        }
    }

    private static uint ChannelMask(int channels) => channels switch
    {
        4 => 0x33,  // quad: FL FR BL BR
        6 => 0x3F,  // 5.1
        8 => 0x63F, // 7.1
        _ => 0x3,   // FL FR
    };

    // -- Native constants -------------------------------------------------
    private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    private const ushort VT_BLOB = 65;

    private static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    // -- Interop structs --------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams { public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams { public int ActivationType; public AudioClientProcessLoopbackParams ProcessLoopbackParams; }

    // Minimal PROPVARIANT laid out for a VT_BLOB payload (x64: cbSize@8, pBlobData@16).
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt; public ushort r1; public ushort r2; public ushort r3;
        public uint cbSize; public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec;
        public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize;
    }

    // WAVEFORMATEXTENSIBLE = WAVEFORMATEX (cbSize=22) + { wValidBitsPerSample, dwChannelMask, SubFormat }.
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatExtensible
    {
        public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec;
        public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize;
        public ushort wValidBitsPerSample; public uint dwChannelMask; public Guid subFormat;
    }

    // -- Interop functions ------------------------------------------------
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // -- COM interfaces (classic ComImport) -------------------------------
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Completed = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Completed.Set();
    }

    // IAudioClient - all 12 methods declared in vtable order; only Initialize/Start/Stop/
    // SetEventHandle/GetService are used, the rest occupy their slots.
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        void GetBufferSize(out uint numBufferFrames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        void IsFormatSupported(int shareMode, ref WaveFormatEx format, IntPtr closestMatch);
        void GetMixFormat(out IntPtr deviceFormat);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint numFramesRead);
        void GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
