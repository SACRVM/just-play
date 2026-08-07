using System;
using System.Runtime.InteropServices;

namespace JustPlay.UI;

/// <summary>
/// macOS Dock attention ("bouncing icon"), the Ladiocast pattern: a critical request keeps
/// the Dock icon bouncing until the user activates the app - and only while the app is in
/// the BACKGROUND, which is exactly the "DJ is in fullscreen Traktor and must notice the
/// stream died" case. No-ops on every other OS and never throws (a missed bounce must not
/// take down the broadcast path).
///
/// Raw objc_msgSend against NSApplication because Avalonia exposes no user-attention API
/// (checked 12.0.3). NSCriticalRequest = 0, cancelUserAttentionRequest stops a pending
/// bounce when the state recovers. Source: NSApplication.h (AppKit).
/// </summary>
public static class MacDock
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr recv, IntPtr sel);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern nint SendInt(IntPtr recv, IntPtr sel, nint arg);

    private static nint _request; // active attention request id (0 = none)

    /// <summary>Bounce the Dock icon until the user activates the app (critical request).
    /// Repeated calls while a request is active are ignored.</summary>
    public static void RequestAttention()
    {
        if (!OperatingSystem.IsMacOS() || _request != 0) return;
        try
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            _request = SendInt(app, sel_registerName("requestUserAttention:"), 0 /* NSCriticalRequest */);
        }
        catch { /* attention is best-effort */ }
    }

    /// <summary>Stop a pending bounce (e.g. the reconnect succeeded before anyone looked).</summary>
    public static void CancelAttention()
    {
        if (!OperatingSystem.IsMacOS() || _request == 0) return;
        try
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            SendInt(app, sel_registerName("cancelUserAttentionRequest:"), _request);
        }
        catch { /* attention is best-effort */ }
        finally { _request = 0; }
    }
}
