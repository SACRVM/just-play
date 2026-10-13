using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace JustPlay.UI;

/// <summary>
/// Cross-platform "keep the display awake" guard for the on-air/recording session. DJ software
/// gets controller input only - the OS sees an idle keyboard/mouse and blanks the screen or
/// locks the box mid-set (Traktor famously doesn't suppress this - observed 2026-07-15). While a
/// JUST app is on air or recording, this holds a display-sleep assertion; released, the normal
/// screensaver/energy policy applies again.
///
/// Windows: SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED).
///   The flag is per-THREAD and sticky until cleared - Enable/Disable must be called from the
///   same thread. Both call sites run in Dispatcher.UIThread.Post handlers, which satisfies that.
/// macOS: IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleDisplaySleep) - the same
///   assertion `caffeinate -d` takes; visible in `pmset -g assertions` under our reason string.
/// Linux: the XDG desktop portal's Inhibit (Idle | Suspend) over the session bus - the one call
///   KDE, GNOME and sandboxed apps all honour, and what browsers take while a video plays.
///   Verified on Plasma 6: it appears in PowerDevil's ActiveInhibitions as "sleep:idle" with our
///   reason - after a ~5 s debounce PowerDevil applies to every new inhibitor - and Request.Close
///   removes it. Without a portal, org.freedesktop.ScreenSaver.Inhibit (blanking and locking).
///   A logind "idle" inhibitor (systemd-inhibit) is NOT enough: KDE's power daemon ignores it.
///   Both live as long as the D-Bus connection that took them, so the calls go over the
///   process-wide session connection, which stays open. A desktop with neither service answers
///   with an error, which is swallowed.
/// Other: no-op (like MacDock - a missing guard must never take down the broadcast path).
///
/// Best-effort and never throws. Enable is idempotent; Disable without Enable is harmless.
/// The OS drops every assertion kind automatically when the process exits.
/// </summary>
public static class KeepAwake
{
    private static bool _active;   // Windows: ES_CONTINUOUS set - macOS: _assertion held
    private static uint _assertion; // macOS IOPMAssertionID (0 = none)
    private static Task<LinuxInhibitor>? _linuxInhibitor; // Linux: the pending/held inhibitor

    // -- Windows ----------------------------------------------------------
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    // -- macOS (IOKit power-management assertion) -------------------------
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
            else if (OperatingSystem.IsLinux())
            {
                // Asynchronous: the UI thread must not wait on the bus. Disable chains onto this
                // task, so a Disable that comes before the replies still releases both.
                _linuxInhibitor = InhibitLinux(reason);
                _active = true;
            }
        }
        catch { _active = false; _assertion = 0; /* staying awake is best-effort */ }
    }

    /// <summary>Release the guard - normal screensaver/energy policy applies again.</summary>
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
            else if (OperatingSystem.IsLinux() && _linuxInhibitor is { } pending)
            {
                _ = pending.ContinueWith(t => UninhibitLinux(t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }
        catch { /* best-effort */ }
        finally { _active = false; _assertion = 0; _linuxInhibitor = null; }
    }

    // -- Linux (session D-Bus) -------------------------------------------
    // Tmds.DBus.Protocol is the D-Bus client Avalonia.FreeDesktop already ships in every app's
    // output - no new binary.

    /// <summary>What was taken: a portal request handle to Close, or a ScreenSaver cookie, or nothing.</summary>
    private readonly record struct LinuxInhibitor(string? PortalHandle, uint ScreenSaverCookie);

    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string ScreenSaver = "org.freedesktop.ScreenSaver";
    private const uint PortalSuspend = 4, PortalIdle = 8;
    private static int _portalRequests;

    private static async Task<LinuxInhibitor> InhibitLinux(string reason)
    {
        var bus = DBusConnection.Session;
        try
        {
            var handle = await bus.CallMethodAsync(PortalInhibit(bus, reason),
                static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null);
            return new LinuxInhibitor(handle, 0);
        }
        catch { /* no portal - try the older interface */ }

        try
        {
            var cookie = await bus.CallMethodAsync(ScreenSaverCall(bus, "Inhibit", reason, 0),
                static (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null);
            return new LinuxInhibitor(null, cookie);
        }
        catch { return default; }
    }

    private static async Task UninhibitLinux(LinuxInhibitor taken)
    {
        var bus = DBusConnection.Session;
        try
        {
            if (taken.PortalHandle is { } handle)
            {
                // (!) The portal answers Inhibit BEFORE it has exported the request object, so a
                // Close sent straight after the reply (an on-air attempt that failed at once) hits
                // UnknownObject and the inhibitor would stay held until the app quits. Measured on
                // Plasma 6: an immediate Close failed this way, one 200 ms later never did.
                for (var attempt = 1; ; attempt++)
                {
                    try { await bus.CallMethodAsync(PortalClose(bus, handle)); break; }
                    catch (DBusErrorReplyException ex) when (attempt < 20 && ex.ErrorName == "org.freedesktop.DBus.Error.UnknownObject")
                    {
                        await Task.Delay(100);
                    }
                }
            }
            else if (taken.ScreenSaverCookie != 0)
            {
                await bus.CallMethodAsync(ScreenSaverCall(bus, "UnInhibit", null, taken.ScreenSaverCookie));
            }
        }
        catch { /* the service went away - and took the inhibitor with it */ }
    }

    // The writer is a ref struct and cannot live across an await, so each message is built here.

    private static MessageBuffer PortalInhibit(DBusConnection bus, string reason)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Inhibit",
                                "Inhibit", "sua{sv}", MessageFlags.None);
        w.WriteString("");                        // no parent window
        w.WriteUInt32(PortalSuspend | PortalIdle);
        var options = w.WriteDictionaryStart();
        w.WriteDictionaryEntryStart();
        w.WriteString("reason");
        w.WriteVariantString(reason);
        // A fresh request path per inhibit. Without a token every request from this connection
        // gets the same ".../t" handle, and an on-air session restarted right after it ended
        // could be closed - or kept - by the previous request's teardown.
        w.WriteDictionaryEntryStart();
        w.WriteString("handle_token");
        w.WriteVariantString($"justkeepawake{Interlocked.Increment(ref _portalRequests)}");
        w.WriteDictionaryEnd(options);
        return w.CreateMessage();
    }

    private static MessageBuffer PortalClose(DBusConnection bus, string handle)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(Portal, handle, "org.freedesktop.portal.Request", "Close", null, MessageFlags.None);
        return w.CreateMessage();
    }

    /// <summary>Inhibit(app, reason) when <paramref name="reason"/> is given, else UnInhibit(cookie).</summary>
    private static MessageBuffer ScreenSaverCall(DBusConnection bus, string member, string? reason, uint cookie)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(ScreenSaver, "/org/freedesktop/ScreenSaver", ScreenSaver, member,
                                reason is null ? "u" : "ss", MessageFlags.None);
        if (reason is null)
        {
            w.WriteUInt32(cookie);
        }
        else
        {
            w.WriteString(AppDomain.CurrentDomain.FriendlyName);
            w.WriteString(reason);
        }
        return w.CreateMessage();
    }
}
