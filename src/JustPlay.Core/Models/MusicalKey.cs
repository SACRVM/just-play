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
}
