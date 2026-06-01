namespace JustPlay.Core.Models;

public enum KeyMode
{
    Major,
    Minor
}

/// <summary>
/// A musical key (one of 24: 12 pitch classes × major/minor).
/// Carries both the conventional name (e.g. "A minor") and the
/// Camelot wheel code (e.g. "8A") that DJs use for harmonic mixing.
/// </summary>
/// <param name="PitchClass">0 = C, 1 = C#, … 11 = B.</param>
public readonly record struct MusicalKey(int PitchClass, KeyMode Mode)
{
    private static readonly string[] PitchNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    // Camelot number per pitch class (index 0..11 = C..B).
    private static readonly int[] CamelotMajor = [8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1];
    private static readonly int[] CamelotMinor = [5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10];

    /// <summary>Conventional name, e.g. "A minor" or "F# major".</summary>
    public string Name =>
        $"{PitchNames[((PitchClass % 12) + 12) % 12]} {(Mode == KeyMode.Major ? "major" : "minor")}";

    /// <summary>Camelot wheel code, e.g. "8A" (A minor) or "8B" (C major).</summary>
    public string Camelot
    {
        get
        {
            var pc = ((PitchClass % 12) + 12) % 12;
            var number = Mode == KeyMode.Major ? CamelotMajor[pc] : CamelotMinor[pc];
            return $"{number}{(Mode == KeyMode.Major ? 'B' : 'A')}";
        }
    }

    public override string ToString() => $"{Name} ({Camelot})";

    /// <summary>
    /// Parse a key string written by another tool — Camelot ("8A", "12B") or musical
    /// ("Am", "F#m", "Bbm", "C", "A minor", "Abmaj") — into a <see cref="MusicalKey"/>,
    /// or null if it can't be understood. Used to interpret the "claimed" key in a
    /// file's tags (e.g. what Mixed In Key / Rekordbox wrote).
    /// </summary>
    public static MusicalKey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        return TryParseCamelot(s) ?? TryParseMusical(s);
    }

    private static MusicalKey? TryParseCamelot(string s)
    {
        if (s.Length is < 2 or > 3) return null;
        var letter = char.ToUpperInvariant(s[^1]);
        if (letter is not ('A' or 'B')) return null;
        if (!int.TryParse(s[..^1], out var num) || num is < 1 or > 12) return null;

        var (table, mode) = letter == 'B' ? (CamelotMajor, KeyMode.Major) : (CamelotMinor, KeyMode.Minor);
        for (var pc = 0; pc < 12; pc++)
            if (table[pc] == num) return new MusicalKey(pc, mode);
        return null;
    }

    private static MusicalKey? TryParseMusical(string s)
    {
        var pc = char.ToUpperInvariant(s[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1,
        };
        if (pc < 0) return null;

        var i = 1;
        if (i < s.Length && s[i] is '#' or '♯') { pc = (pc + 1) % 12; i++; }
        else if (i < s.Length && s[i] is 'b' or '♭') { pc = (pc + 11) % 12; i++; }

        var rest = s[i..].Trim().ToLowerInvariant();
        var mode = rest.Length == 0 || rest.StartsWith("maj") ? KeyMode.Major
                 : rest.StartsWith('m') ? KeyMode.Minor
                 : (KeyMode?)null;
        return mode is { } m ? new MusicalKey(pc, m) : null;
    }
}
