using System;
using Avalonia.Media;

namespace JustPlay.UI.Theming;

/// <summary>
/// The single source of truth for Camelot key colours - the 12-hue mixing-wheel palette, shared by the
/// <see cref="Controls.KeyWheel"/> and the key-badge converter so the wheel and every "8A" pill can never
/// drift apart again (Chloe 2026-07-07). Same number = same hue (evenly around the circle of fifths);
/// A/minor sits a touch darker than B/major, matching the wheel's inner/outer rings.
/// </summary>
public static class CamelotPalette
{
    /// <summary>Resting fill colour for a Camelot number (1..12); A is slightly darker than B, same hue.</summary>
    public static Color ForNumber(int n, bool isA)
    {
        var hue = (Math.Clamp(n, 1, 12) - 1) / 12.0 * 360.0;
        return Hsl(hue, 0.62, isA ? 0.45 : 0.53);
    }

    /// <summary>Colour for a Camelot code like "8A" / "12B"; null when it isn't a valid code.</summary>
    public static Color? ForCode(string? code) =>
        TryParse(code, out var n, out var isA) ? ForNumber(n, isA) : null;

    public static bool TryParse(string? code, out int n, out bool isA)
    {
        n = 0; isA = true;
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim();
        var last = char.ToUpperInvariant(code[^1]);
        if (last is not ('A' or 'B')) return false;
        isA = last == 'A';
        return int.TryParse(code[..^1], out n) && n is >= 1 and <= 12;
    }

    /// <summary>HSL -> opaque Color (h in degrees, s/l in 0..1).</summary>
    public static Color Hsl(double h, double s, double l, byte a = 0xFF)
    {
        h = ((h % 360) + 360) % 360;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = l - c / 2;
        double r = 0, g = 0, b = 0;
        switch ((int)(h / 60))
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        return Color.FromArgb(a, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
