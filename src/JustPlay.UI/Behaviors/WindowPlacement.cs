using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using JustPlay.Core.Storage;

namespace JustPlay.UI.Behaviors;

/// <summary>Persisted window placement: position in physical PIXELS (X,Y), size in DIPs (W,H -
/// screen-independent, so it restores correctly across monitors of different scaling).</summary>
public sealed record WindowBounds(int X, int Y, double W, double H);

/// <summary>
/// Suite-wide "smart reopening": every non-dialog JUST window remembers its
/// position + size, and on reopen is validated against the CURRENT monitor set - if the saved spot is
/// off-screen or bigger than the target screen (monitor unplugged, resolution changed, laptop docked),
/// it's shrunk / nudged back onto a visible work area. One shared store,
/// <c>%LOCALAPPDATA%\JUST\window-layout.json</c>, keyed per window. About / Oops / Input / progress
/// dialogs opt OUT simply by not calling <see cref="Track"/>.
///
/// Reflection-free (source-generated JSON) so the suite stays trim/AOT-safe.
/// </summary>
public static class WindowPlacement
{
    private static readonly string StorePath =
        JustDataPaths.Combine("JUST", "window-layout.json");

    private static readonly Dictionary<string, WindowBounds> Cache = Load();

    /// <summary>
    /// Persist <paramref name="window"/>'s bounds under <paramref name="key"/> and restore them
    /// (smart-validated) on open. <paramref name="canPersist"/> lets a window skip SAVING while it's in
    /// a non-restorable mode (e.g. JUST PLAY's fixed-size MINI mode) - return false to not record the
    /// current bounds. Call once, from the window constructor (after InitializeComponent), so the saved
    /// bounds are applied before the window is shown - no reposition flicker.
    /// <para>Returns whether a saved size was restored. A window that sizes itself to its content on
    /// first run needs to know: doing that on top of a remembered size would throw the user's own
    /// choice away every time they open it.</para>
    /// </summary>
    public static bool Track(Window window, string key, Func<bool>? canPersist = null)
    {
        Cache.TryGetValue(key, out var latest);
        if (latest is not null) Apply(window, latest); // set before Show -> window appears at the saved spot
        var restoredSize = latest is not null && window.CanResize;

        void Capture()
        {
            // Only snapshot a genuinely "normal", restorable state - never a maximized/minimized frame
            // or a mode the window says isn't persistable (mini). Guards a 0-size frame during teardown.
            if (window.WindowState != WindowState.Normal) return;
            if (window is IFramelessWindow { IsMaximized: true }) return;
            if (canPersist is not null && !canPersist()) return;
            if (window.Width <= 0 || window.Height <= 0) return;
            latest = new WindowBounds(window.Position.X, window.Position.Y, window.Width, window.Height);
        }

        window.Opened += (_, _) => EnsureVisible(window); // clamp to the monitors that exist NOW
        window.PositionChanged += (_, _) => Capture();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Layoutable.WidthProperty || e.Property == Layoutable.HeightProperty
                || e.Property == Window.WindowStateProperty)
                Capture();
        };
        window.Closing += (_, _) =>
        {
            if (latest is null) return;
            Cache[key] = latest;
            Save();
        };

        return restoredSize;
    }

    private static void Apply(Window w, WindowBounds b)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual; // otherwise the platform re-centres
        // Fixed-size windows: the DESIGN owns the size - restoring a stale saved size would
        // squash a newer, taller layout (JUST BOOTLEG grew 640->860 and reopened crushed,
        // 2026-07-12). For them only the position is history; the size never is.
        if (w.CanResize)
        {
            w.Width = b.W;
            w.Height = b.H;
        }
        w.Position = new PixelPoint(b.X, b.Y);
    }

    /// <summary>
    /// Clamp the window fully onto a visible work area of the CURRENT monitors: shrink it if it's bigger
    /// than the target screen, move it if it's (partly) off every screen. Safe with or without saved
    /// bounds - a default-centred, already-visible window is left untouched.
    /// </summary>
    public static void EnsureVisible(Window w)
    {
        var screens = w.Screens;
        if (screens is null || screens.All.Count == 0) return;

        var scale = w.RenderScaling <= 0 ? 1.0 : w.RenderScaling;
        int wpx = Math.Max(1, (int)Math.Round(w.Width * scale));
        int hpx = Math.Max(1, (int)Math.Round(w.Height * scale));
        var rect = new PixelRect(w.Position.X, w.Position.Y, wpx, hpx);

        // Pick the screen the window overlaps most; if it overlaps none (fully off-screen), fall back to
        // the primary - either way we then clamp the window fully inside that screen's work area.
        var all = screens.All;
        int bestIdx = -1;
        long bestOverlap = -1;
        for (int i = 0; i < all.Count; i++)
        {
            long area = Overlap(all[i].WorkingArea, rect);
            if (area > bestOverlap) { bestOverlap = area; bestIdx = i; }
        }
        var target = bestIdx >= 0 ? all[bestIdx] : (screens.Primary ?? all[0]);
        var wa = target.WorkingArea;

        // Shrink to fit the work area if the saved size exceeds this monitor.
        if (wpx > wa.Width)  { wpx = wa.Width;  w.Width  = wpx / scale; }
        if (hpx > wa.Height) { hpx = wa.Height; w.Height = hpx / scale; }

        // Clamp the top-left so the whole window sits inside the work area.
        int x = Math.Clamp(rect.X, wa.X, wa.X + Math.Max(0, wa.Width  - wpx));
        int y = Math.Clamp(rect.Y, wa.Y, wa.Y + Math.Max(0, wa.Height - hpx));
        if (x != w.Position.X || y != w.Position.Y)
            w.Position = new PixelPoint(x, y);
    }

    /// <summary>Overlap area (px^2) of two pixel rects - 0 when they don't intersect.</summary>
    private static long Overlap(PixelRect a, PixelRect b)
    {
        int ix = Math.Max(a.X, b.X);
        int iy = Math.Max(a.Y, b.Y);
        int right  = Math.Min(a.X + a.Width,  b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return (long)Math.Max(0, right - ix) * Math.Max(0, bottom - iy);
    }

    private static Dictionary<string, WindowBounds> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize(File.ReadAllText(StorePath),
                    WindowLayoutJsonContext.Default.DictionaryStringWindowBounds) ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowPlacement] load failed, ignoring: {ex.GetType().Name}: {ex.Message}");
        }
        return new();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                Cache, WindowLayoutJsonContext.Default.DictionaryStringWindowBounds));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowPlacement] save failed, ignoring: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, WindowBounds>))]
internal sealed partial class WindowLayoutJsonContext : JsonSerializerContext;
