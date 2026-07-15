// juststream_capture.m — per-application audio capture for macOS via Core Audio
// process taps (macOS 14.2+, shipped to users on 14.4+). The macOS counterpart of
// WasapiProcessLoopbackCapture: tap ONE process's rendered audio invisibly (the app
// keeps playing to its own device), deliver float32 PCM to a callback.
//
// Flat C ABI, consumed by MacProcessAudioCapture.cs via P/Invoke. One tap at a time
// (matches IProcessAudioCapture semantics). Plan + API references:
// research/macos-app-audio-capture.md (AudioCap, sudara's gist, Apple docs).
//
// SDK NOTE: this file compiles against SDK 13.3 (the Xcode on the build mini) even
// though the tap API is 14.2+ — the new selectors/keys are declared below with values
// verified against the 14.5 SDK headers (alexey-lysiuk/macos-sdk mirror, 2026-07-15),
// CATapDescription is resolved via NSClassFromString, and the two tap functions via
// dlsym. All of it exists at RUNTIME on the target machines; C# gates on 14.4.
//
// Build: build/make-capture-shim.sh → ../osx/libjuststream_capture.dylib (universal).
// TCC: first AudioDeviceStart prompts "System Audio Recording Only" — the host app
// bundle needs NSAudioCaptureUsageDescription and a code signature or the prompt
// silently never fires (same trap as the mic one, learned 2026-07-15).

#import <Foundation/Foundation.h>
#import <CoreAudio/CoreAudio.h>
#include <dlfcn.h>
#include <libproc.h>
#include <string.h>

// ── 14.2+ API surface missing from SDK 13.3 (values verified, see header note) ──

enum {
    jscProcessObjectList   = 'prs#',  // kAudioHardwarePropertyProcessObjectList
    jscTranslatePIDToObj   = 'id2p',  // kAudioHardwarePropertyTranslatePIDToProcessObject
    jscProcessPID          = 'ppid',  // kAudioProcessPropertyPID
    jscProcessBundleID     = 'pbid',  // kAudioProcessPropertyBundleID
    jscProcessIsRunningOut = 'piro',  // kAudioProcessPropertyIsRunningOutput
    jscProcessDevices      = 'pdv#',  // kAudioProcessPropertyDevices
    jscTapFormat           = 'tfmt',  // kAudioTapPropertyFormat
};

#define JSC_kTapListKey        "taps"          // kAudioAggregateDeviceTapListKey
#define JSC_kTapAutoStartKey   "tapautostart"  // kAudioAggregateDeviceTapAutoStartKey
#define JSC_kSubTapUIDKey      "uid"           // kAudioSubTapUIDKey
#define JSC_kSubTapDriftKey    "drift"         // kAudioSubTapDriftCompensationKey

// CATapDescription selectors (verified against CATapDescription.h 14.5):
//   initStereoMixdownOfProcesses:, name, UUID, privateTap (setPrivate:), muteBehavior.
// Declared as an NSObject category so the compiler accepts dynamic dispatch.
@interface NSObject (JSCTapDescription)
- (instancetype)initStereoMixdownOfProcesses:(NSArray<NSNumber *> *)processObjectIDs;
- (instancetype)initWithProcesses:(NSArray<NSNumber *> *)processObjectIDs
                     andDeviceUID:(NSString *)deviceUID
                       withStream:(NSInteger)stream;   // full channel set of that device stream
- (void)setName:(NSString *)name;
- (void)setPrivate:(BOOL)isPrivate;                 // property privateTap
- (void)setMuteBehavior:(NSInteger)behavior;        // 0 unmuted · 1 muted · 2 mutedWhenTapped
- (NSUUID *)UUID;
@end

typedef OSStatus (*jsc_create_tap_fn)(id description, AudioObjectID *outTapID);
typedef OSStatus (*jsc_destroy_tap_fn)(AudioObjectID tapID);

static jsc_create_tap_fn  jsc_create_tap;
static jsc_destroy_tap_fn jsc_destroy_tap;

static int jsc_resolve(void) {
    if (jsc_create_tap && jsc_destroy_tap) return 1;
    void *h = dlopen("/System/Library/Frameworks/CoreAudio.framework/CoreAudio", RTLD_LAZY);
    if (!h) return 0;
    jsc_create_tap  = (jsc_create_tap_fn)dlsym(h, "AudioHardwareCreateProcessTap");
    jsc_destroy_tap = (jsc_destroy_tap_fn)dlsym(h, "AudioHardwareDestroyProcessTap");
    return jsc_create_tap && jsc_destroy_tap && NSClassFromString(@"CATapDescription") != nil;
}

// ── ABI ──────────────────────────────────────────────────────────────────────

typedef struct {
    int32_t pid;
    int32_t is_playing;      // process is currently emitting audio (IsRunningOutput)
    char    name[128];       // executable name (no path), UTF-8
    char    bundle_id[256];  // may be empty (non-bundled processes)
} jsc_process_info;

// interleaved float32; buffer only valid for the duration of the call
typedef void (*jsc_audio_cb)(const float *pcm, int32_t frames, int32_t channels,
                             double sample_rate, void *user);

// ── state (one tap at a time) ────────────────────────────────────────────────

static AudioObjectID       g_tap    = kAudioObjectUnknown;
static AudioObjectID       g_agg    = kAudioObjectUnknown;
static AudioDeviceIOProcID g_ioproc = NULL;
static jsc_audio_cb        g_cb     = NULL;
static void               *g_user   = NULL;
static double              g_rate   = 0;
static int32_t             g_chans  = 2;

static AudioObjectPropertyAddress jsc_addr(AudioObjectPropertySelector sel) {
    return (AudioObjectPropertyAddress){ sel, kAudioObjectPropertyScopeGlobal,
                                         kAudioObjectPropertyElementMain };
}

// ── availability (for IsSupported on the C# side) ────────────────────────────

int32_t jsc_is_supported(void) { return jsc_resolve() ? 1 : 0; }

// ── process enumeration ──────────────────────────────────────────────────────

int32_t jsc_list_processes(jsc_process_info *out, int32_t cap) {
    if (!jsc_resolve()) return -1;
    AudioObjectPropertyAddress a = jsc_addr(jscProcessObjectList);
    UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &a, 0, NULL, &size) != noErr)
        return -1;
    UInt32 count = size / sizeof(AudioObjectID);
    if (count == 0) return 0;
    AudioObjectID *objs = malloc(size);
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, 0, NULL, &size, objs) != noErr) {
        free(objs);
        return -1;
    }
    count = size / sizeof(AudioObjectID);

    int32_t n = 0;
    for (UInt32 i = 0; i < count && n < cap; i++) {
        jsc_process_info *pi = &out[n];
        memset(pi, 0, sizeof(*pi));

        AudioObjectPropertyAddress pa = jsc_addr(jscProcessPID);
        pid_t pid = 0; UInt32 psz = sizeof(pid);
        if (AudioObjectGetPropertyData(objs[i], &pa, 0, NULL, &psz, &pid) != noErr || pid <= 0)
            continue;
        pi->pid = pid;

        pa = jsc_addr(jscProcessIsRunningOut);
        UInt32 running = 0, rsz = sizeof(running);
        if (AudioObjectGetPropertyData(objs[i], &pa, 0, NULL, &rsz, &running) == noErr)
            pi->is_playing = running ? 1 : 0;

        pa = jsc_addr(jscProcessBundleID);
        CFStringRef bid = NULL; UInt32 bsz = sizeof(bid);
        if (AudioObjectGetPropertyData(objs[i], &pa, 0, NULL, &bsz, &bid) == noErr && bid) {
            CFStringGetCString(bid, pi->bundle_id, sizeof(pi->bundle_id), kCFStringEncodingUTF8);
            CFRelease(bid);
        }

        proc_name(pid, pi->name, sizeof(pi->name));
        if (pi->name[0] == '\0')
            continue; // process died between list + query
        n++;
    }
    free(objs);
    return n;
}

// ── IOProc — the tap audio arrives as the aggregate's INPUT buffers ─────────

static OSStatus jsc_io_proc(AudioObjectID device,
                            const AudioTimeStamp *now,
                            const AudioBufferList *input, const AudioTimeStamp *inputTime,
                            AudioBufferList *output, const AudioTimeStamp *outputTime,
                            void *client) {
    (void)device; (void)now; (void)inputTime; (void)output; (void)outputTime; (void)client;
    if (g_cb && input && input->mNumberBuffers > 0) {
        const AudioBuffer *b = &input->mBuffers[0];
        int32_t chans = (int32_t)b->mNumberChannels;
        if (chans > 0 && b->mData && b->mDataByteSize > 0) {
            int32_t frames = (int32_t)(b->mDataByteSize / (sizeof(float) * (UInt32)chans));
            g_cb((const float *)b->mData, frames, chans, g_rate, g_user);
        }
    }
    return noErr;
}

// ── start / stop ─────────────────────────────────────────────────────────────

void jsc_stop_tap(void) {
    if (g_agg != kAudioObjectUnknown && g_ioproc) {
        AudioDeviceStop(g_agg, g_ioproc);
        AudioDeviceDestroyIOProcID(g_agg, g_ioproc);
    }
    g_ioproc = NULL;
    if (g_agg != kAudioObjectUnknown) {
        // Never leak the private aggregate — a leaked one persists until reboot and
        // its UID then collides on the next create (research doc, gotchas).
        AudioHardwareDestroyAggregateDevice(g_agg);
        g_agg = kAudioObjectUnknown;
    }
    if (g_tap != kAudioObjectUnknown && jsc_destroy_tap) {
        jsc_destroy_tap(g_tap);
        g_tap = kAudioObjectUnknown;
    }
    g_cb = NULL; g_user = NULL; g_rate = 0;
}

double  jsc_tap_sample_rate(void) { return g_rate; }
int32_t jsc_tap_channels(void)    { return g_chans; }

// The device UID this process is rendering to (its first device), or nil.
// A multi-out DJ app's Master/Cue pairs live on ONE device — tapping that device's
// stream (instead of a stereo mixdown) preserves the channel layout for extraction.
static NSString *jsc_process_device_uid(AudioObjectID proc) {
    AudioObjectPropertyAddress a = jsc_addr(jscProcessDevices);
    UInt32 size = 0;
    OSStatus st = AudioObjectGetPropertyDataSize(proc, &a, 0, NULL, &size);
    if (st != noErr || size < sizeof(AudioObjectID)) {
        fprintf(stderr, "[jsc] process devices: st=%d size=%u (none)\n", (int)st, size);
        return nil;
    }
    AudioObjectID *devs = malloc(size);
    NSString *uid = nil;
    st = AudioObjectGetPropertyData(proc, &a, 0, NULL, &size, devs);
    if (st == noErr) {
        UInt32 count = size / sizeof(AudioObjectID);
        fprintf(stderr, "[jsc] process devices: %u\n", count);
        for (UInt32 i = 0; i < count && !uid; i++) {
            AudioObjectPropertyAddress ua = jsc_addr(kAudioDevicePropertyDeviceUID);
            CFStringRef s = NULL; UInt32 usz = sizeof(s);
            if (AudioObjectGetPropertyData(devs[i], &ua, 0, NULL, &usz, &s) == noErr && s)
                uid = (__bridge_transfer NSString *)s;
        }
        fprintf(stderr, "[jsc] process device uid: %s\n", uid ? uid.UTF8String : "(nil)");
    }
    free(devs);
    return uid;
}

// Fallback device discovery: the REAL output device with the most channels. On this
// machine class (DJ setup) that's the multi-out DJ interface. Aggregates are skipped —
// a broken user aggregate can have MORE channels than the real interface and tapping a
// device the process doesn't render to yields silence. Used when 'pdv#' comes back
// empty, which it does for at least Traktor Pro 4 even while audibly rendering
// (observed macOS 15.7, 2026-07-15).
static NSString *jsc_best_output_device_uid(void) {
    AudioObjectPropertyAddress a = jsc_addr(kAudioHardwarePropertyDevices);
    UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &a, 0, NULL, &size) != noErr)
        return nil;
    UInt32 count = size / sizeof(AudioObjectID);
    AudioObjectID *devs = malloc(size);
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, 0, NULL, &size, devs) != noErr) {
        free(devs);
        return nil;
    }
    NSString *bestUID = nil;
    UInt32 bestChans = 2; // require MORE than stereo — otherwise not worth a device tap
    for (UInt32 i = 0; i < count; i++) {
        AudioObjectPropertyAddress ta = jsc_addr(kAudioDevicePropertyTransportType);
        UInt32 transport = 0, tsz = sizeof(transport);
        if (AudioObjectGetPropertyData(devs[i], &ta, 0, NULL, &tsz, &transport) == noErr
            && transport == kAudioDeviceTransportTypeAggregate)
            continue;

        AudioObjectPropertyAddress sa = { kAudioDevicePropertyStreamConfiguration,
                                          kAudioObjectPropertyScopeOutput,
                                          kAudioObjectPropertyElementMain };
        UInt32 bsz = 0;
        if (AudioObjectGetPropertyDataSize(devs[i], &sa, 0, NULL, &bsz) != noErr || bsz == 0)
            continue;
        AudioBufferList *abl = malloc(bsz);
        UInt32 chans = 0;
        if (AudioObjectGetPropertyData(devs[i], &sa, 0, NULL, &bsz, abl) == noErr)
            for (UInt32 b = 0; b < abl->mNumberBuffers; b++)
                chans += abl->mBuffers[b].mNumberChannels;
        free(abl);
        if (chans <= bestChans) continue;

        AudioObjectPropertyAddress ua = jsc_addr(kAudioDevicePropertyDeviceUID);
        CFStringRef s = NULL; UInt32 usz = sizeof(s);
        if (AudioObjectGetPropertyData(devs[i], &ua, 0, NULL, &usz, &s) == noErr && s) {
            bestUID = (__bridge_transfer NSString *)s;
            bestChans = chans;
        }
    }
    free(devs);
    fprintf(stderr, "[jsc] best multi-out device: %s (%u ch)\n",
            bestUID ? bestUID.UTF8String : "(none)", bestChans);
    return bestUID;
}

// Returns 0 on success, a negative jsc error on failure:
//   -1 pid unknown to Core Audio  -2 tap create  -3 aggregate  -4 ioproc/start  -5 unsupported OS
// full_channels: 0 = stereo mixdown of everything the process plays; 1 = tap the process's
// output DEVICE stream with its full channel count (Master/Cue pairs stay separate — the
// caller extracts the pair it wants). Falls back to mixdown when no device is found.
int32_t jsc_start_tap(int32_t pid, int32_t mute, int32_t full_channels,
                      jsc_audio_cb cb, void *user) {
    if (!jsc_resolve()) return -5;
    jsc_stop_tap();
    @autoreleasepool {
        // pid → Core Audio process object
        AudioObjectPropertyAddress a = jsc_addr(jscTranslatePIDToObj);
        AudioObjectID proc = kAudioObjectUnknown;
        UInt32 sz = sizeof(proc);
        pid_t qpid = pid;
        OSStatus st = AudioObjectGetPropertyData(kAudioObjectSystemObject, &a,
                                                 sizeof(qpid), &qpid, &sz, &proc);
        if (st != noErr || proc == kAudioObjectUnknown) return -1;

        // Tap of exactly this process. Private so no other app sees it.
        Class descCls = NSClassFromString(@"CATapDescription");
        id desc = nil;
        if (full_channels) {
            // 'pdv#' only lists devices the process is rendering to RIGHT NOW — and for
            // some hosts (Traktor Pro 4) it stays empty even mid-playback. Try it briefly,
            // then fall back to the best real multi-out device (the DJ interface).
            NSString *procDev = nil;
            for (int try = 0; try < 3 && !procDev; try++) {
                procDev = jsc_process_device_uid(proc);
                if (!procDev) usleep(150 * 1000);
            }
            if (!procDev) procDev = jsc_best_output_device_uid();
            if (procDev) {
                desc = [[descCls alloc] initWithProcesses:@[ @(proc) ]
                                             andDeviceUID:procDev
                                               withStream:0];
                if (!desc) fprintf(stderr, "[jsc] device-tap init returned nil for %s\n", procDev.UTF8String);
            }
        }
        BOOL deviceTap = desc != nil;
        if (!desc)
            desc = [[descCls alloc] initStereoMixdownOfProcesses:@[ @(proc) ]];
        if (!desc) return -2;
        [desc setName:@"JUST STREAM app tap"];
        [desc setPrivate:YES];
        [desc setMuteBehavior:(mute ? 1 : 0)];   // CATapMuted : CATapUnmuted
        fprintf(stderr, "[jsc] tap pid=%d mode=%s (requested %s)\n", pid,
                deviceTap ? "device-stream" : "stereo-mixdown",
                full_channels ? "full-channels" : "mixdown");

        st = jsc_create_tap(desc, &g_tap);
        if (st != noErr || g_tap == kAudioObjectUnknown) { g_tap = kAudioObjectUnknown; return -2; }

        // Tap format (rate follows the device the target renders to).
        AudioStreamBasicDescription asbd = {0};
        a = jsc_addr(jscTapFormat);
        sz = sizeof(asbd);
        if (AudioObjectGetPropertyData(g_tap, &a, 0, NULL, &sz, &asbd) == noErr && asbd.mSampleRate > 0) {
            g_rate  = asbd.mSampleRate;
            g_chans = (int32_t)asbd.mChannelsPerFrame;
        } else {
            g_rate = 48000; g_chans = 2;
        }
        fprintf(stderr, "[jsc] tap format: %.0f Hz, %d ch\n", g_rate, g_chans);

        // Default output device UID = the aggregate's clock master.
        a = jsc_addr(kAudioHardwarePropertyDefaultOutputDevice);
        AudioObjectID dev = kAudioObjectUnknown;
        sz = sizeof(dev);
        AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, 0, NULL, &sz, &dev);
        NSString *devUID = @"";
        if (dev != kAudioObjectUnknown) {
            a = jsc_addr(kAudioDevicePropertyDeviceUID);
            CFStringRef uid = NULL;
            sz = sizeof(uid);
            if (AudioObjectGetPropertyData(dev, &a, 0, NULL, &sz, &uid) == noErr && uid)
                devUID = (__bridge_transfer NSString *)uid;
        }

        // Private aggregate hosting the tap; drift compensation resyncs tap vs clock.
        NSDictionary *aggDict = @{
            @kAudioAggregateDeviceNameKey          : @"JUST STREAM Tap",
            @kAudioAggregateDeviceUIDKey           : [[NSUUID UUID] UUIDString],
            @kAudioAggregateDeviceMainSubDeviceKey : devUID,
            @kAudioAggregateDeviceIsPrivateKey     : @YES,
            @JSC_kTapAutoStartKey                  : @YES,
            @kAudioAggregateDeviceSubDeviceListKey : @[ @{ @kAudioSubDeviceUIDKey : devUID } ],
            @JSC_kTapListKey                       : @[ @{
                @JSC_kSubTapUIDKey   : [[desc UUID] UUIDString],
                @JSC_kSubTapDriftKey : @YES } ],
        };
        st = AudioHardwareCreateAggregateDevice((__bridge CFDictionaryRef)aggDict, &g_agg);
        if (st != noErr || g_agg == kAudioObjectUnknown) { jsc_stop_tap(); return -3; }

        g_cb = cb; g_user = user;
        st = AudioDeviceCreateIOProcID(g_agg, jsc_io_proc, NULL, &g_ioproc);
        if (st != noErr || !g_ioproc) { jsc_stop_tap(); return -4; }
        st = AudioDeviceStart(g_agg, g_ioproc);   // ← first call fires the TCC prompt
        if (st != noErr) { jsc_stop_tap(); return -4; }
        return 0;
    }
}
