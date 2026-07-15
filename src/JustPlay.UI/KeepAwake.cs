using System;
using System.Runtime.InteropServices;

namespace JustPlay.UI;

/// <summary>
/// Cross-platform "keep the display awake" guard for the on-air/recording session. DJ software
/// gets controller input only — the OS sees an idle keyboard/mouse and blanks the screen or
/// locks the box mid-set (Traktor famously doesn't suppress this; Chloe 2026-07-15). While a
/// JUST app is on air or recording, this holds a display-sleep assertion; released, the normal
/// screensaver/energy policy applies again.
///
/// Windows: SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED).
///   The flag is per-THREAD and sticky until cleared — Enable/Disable must be called from the
///   same thread. Both call sites run in Dispatcher.UIThread.Post handlers, which satisfies that.
/// macOS: IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleDisplaySleep) — the same
///   assertion `caffeinate -d` takes; visible in `pmset -g assertions` under our reason string.
/// Linux/other: no-op (like MacDock — a missing guard must never take down the broadcast path).
///
/// Best-effort and never throws. Enable is idempotent; Disable without Enable is harmless.
/// The OS drops both assertion kinds automatically when the process exits.
/// </summary>
public static class KeepAwake
{
    private static bool _active;   // Windows: ES_CONTINUOUS set · macOS: _assertion held
    private static uint _assertion; // macOS IOPMAssertionID (0 = none)

    // ── Windows ──────────────────────────────────────────────────────────
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    // ── macOS (IOKit power-management assertion) ─────────────────────────
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCfStringEncodingUtf8 = 0x08000100;
    private const uint KIOPMAssertionLevelOn = 255;

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);
    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint id);

    /// <summary>Hold the display awake. Idempotent; call from the UI thread (see class remarks).</summary>
    public static void Enable(string reason)
    {
        if (_active) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _active = SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired) != 0;
            }
            else if (OperatingSystem.IsMacOS())
            {
                var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleDisplaySleep", KCfStringEncodingUtf8);
                var name = CFStringCreateWithCString(IntPtr.Zero, reason, KCfStringEncodingUtf8);
                try
                {
                    _active = IOPMAssertionCreateWithName(type, KIOPMAssertionLevelOn, name, out _assertion) == 0;
                    if (!_active) _assertion = 0;
                }
                finally
                {
                    if (type != IntPtr.Zero) CFRelease(type);
                    if (name != IntPtr.Zero) CFRelease(name);
                }
            }
        }
        catch { _active = false; _assertion = 0; /* staying awake is best-effort */ }
    }

    /// <summary>Release the guard — normal screensaver/energy policy applies again.</summary>
    public static void Disable()
    {
        if (!_active) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetThreadExecutionState(EsContinuous); // clear DISPLAY/SYSTEM, keep nothing pinned
            }
            else if (OperatingSystem.IsMacOS() && _assertion != 0)
            {
                IOPMAssertionRelease(_assertion);
            }
        }
        catch { /* best-effort */ }
        finally { _active = false; _assertion = 0; }
    }
}
