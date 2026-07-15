# Per-app audio capture on macOS (JUST STREAM "App" source)

*Researched 2026-07-15. Fills the `IProcessAudioCapture` slot that WASAPI process
loopback fills on Windows (`WasapiProcessLoopbackCapture` → `NullProcessAudioCapture`
on non-Windows today, see `src/JustPlay.Stream/Program.cs`).*

## TL;DR

macOS has a true per-process, audio-only equivalent of WASAPI process loopback:
**Core Audio process taps** (`AudioHardwareCreateProcessTap` + private aggregate
device), API floor macOS 14.2, practical floor **14.4** (settled TCC prompt/category).
Fallback for macOS 13.0–14.3: **ScreenCaptureKit audio** (`SCStream` with
`capturesAudio`, per-app `SCContentFilter`) — what OBS ships. Below 13: nothing
per-process (only whole-system virtual drivers like BlackHole). BASS itself offers
no loopback on macOS (`BASS_RecordStart` loopback is Windows-only) — its role stays
receiving PCM via `BASS_StreamPutData` on a `STREAMPROC_PUSH` stream, DSP + encode
unchanged.

## Recommended architecture

One small native shim dylib (`libjuststream_capture.dylib`, C/Obj-C, ~300 lines),
flat C ABI, P/Invoked from a new adapter (e.g. `JustPlay.Audio.MacCapture`)
implementing `IProcessAudioCapture`:

```c
int  jsc_list_processes(jsc_process_info* buf, int cap);   // pid, bundle_id, name, is_audio_active
int  jsc_start_tap(pid_t pid, jsc_audio_cb cb, void* user, // cb(float* interleaved, int frames, int chans, double rate)
                   jsc_format_changed_cb fmt_cb, int mute_source);
void jsc_stop_tap(void);
int  jsc_start_sck(const char* bundle_id, jsc_audio_cb cb, void* user); // 13.0–14.3 fallback
```

Why a shim instead of pure P/Invoke: the tap path is ~95% plain C, but
`CATapDescription` is Objective-C, the aggregate-device dictionary is CFDictionary
boilerplate, and the ScreenCaptureKit fallback (delegates/blocks/`CMSampleBuffer`)
is impossible without a shim anyway. Runtime pick:
`>= 14.4 → tap`, `>= 13.0 → SCK`, else hide the "App" source (as today).
Pin the audio callback delegate as a field — same rule as `_endSync` in
`BassAudioEngine.cs`.

## Process-tap call sequence (macOS 14.2+)

Enumeration is plain C via `AudioObjectGetPropertyData` on `kAudioObjectSystemObject`:
`kAudioHardwarePropertyProcessObjectList` (all process objects),
`kAudioHardwarePropertyTranslatePIDToProcessObject` (PID → object), then per object
`kAudioProcessPropertyPID` / `kAudioProcessPropertyBundleID` /
`kAudioProcessPropertyIsRunning` (is it playing audio right now — use for the picker).

1. `CATapDescription` (Obj-C): `initStereoMixdownOfProcesses:@[processObjectID]`,
   set `muteBehavior`, `setPrivate:YES`, read its `UUID`.
2. `AudioHardwareCreateProcessTap(desc, &tapID)`.
3. Read `kAudioTapPropertyFormat` → `AudioStreamBasicDescription` (float32; rate
   follows the output device the target app plays to).
4. Private aggregate device (CFDictionary): main sub-device = current default output
   UID, `kAudioAggregateDeviceIsPrivateKey = true`, `TapAutoStartKey = true`,
   `kAudioAggregateDeviceTapListKey = [{ SubTapUID: tapUUID, DriftCompensation: true }]`.
5. `AudioHardwareCreateAggregateDevice(dict, &aggID)`.
6. `AudioDeviceCreateIOProcID` + `AudioDeviceStart` → IOProc delivers interleaved
   float32 `AudioBufferList` → `BASS_StreamPutData`.
7. Teardown in reverse; **always destroy the aggregate** (leaked private aggregates
   persist until reboot; duplicate UID → OSStatus error on next create).

## Permissions / packaging

- Info.plist: `NSAudioCaptureUsageDescription`. No sandbox, no special entitlement;
  works in a non-sandboxed, notarized Developer-ID app (AudioCap is proof).
- TCC prompt fires on first `AudioDeviceStart`; grant lands under Privacy & Security
  → Screen & System Audio Recording → **"System Audio Recording Only"** — does NOT
  get Sequoia's monthly re-confirmation nag or the menu-bar recording pill (SCK's
  Screen Recording grant does).
- **Unsigned builds fail silently** (no prompt, silence delivered) — ad-hoc-sign dev
  builds. Reset for testing: `tccutil reset SystemAudioCaptureRequests <bundle-id>`.
- No public "query permission" API; just let the OS prompt (AudioCap's status check
  uses private TCC API — avoid in a notarized app).

## Gotchas

- Tap format tracks the target's output device — listen for `kAudioTapPropertyFormat`
  / default-device changes and recreate the push stream (the #1 reported pain point).
- SCK audio-only still wants a dummy video sink attached (OBS drops the frames).
- SCK caps at 2ch/48kHz; needs the heavier Screen Recording grant.
- Known Core Audio bug: 4+-channel default output halves volume on stereo-mixdown
  global taps (sudara's gist).
- Apps bypassing Core Audio output (exclusive-mode HAL) can't be tapped (same for SCK).
- IOProc latency ≈ device IO buffer (~5–10 ms) — fine for the broadcast chain.

## References

- AudioCap (canonical tap sample, Swift — port to C):
  https://github.com/insidegui/AudioCap
- sudara's Obj-C/C tap gist (MiniMeters, closest to raw C):
  https://gist.github.com/sudara/34f00efad69a7e8ceafa078ea0f76f6f
- OBS SCK audio source (the 13.x fallback blueprint):
  https://github.com/obsproject/obs-studio/blob/master/plugins/mac-capture/mac-sck-audio-capture.m
- Apple: https://developer.apple.com/documentation/CoreAudio/capturing-system-audio-with-core-audio-taps
- miniaudio integration discussion: https://github.com/mackron/miniaudio/issues/875
- BASS loopback is Windows-only: https://www.un4seen.com/doc/bass/BASS_RecordStart.html
- No .NET bindings exist for process taps as of mid-2026 — the shim keeps that cheap.
