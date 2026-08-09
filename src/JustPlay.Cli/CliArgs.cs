namespace JustPlay.Cli;

/// <summary>
/// The CLI's flag parsing - one implementation for every verb.
///
/// <para>These four helpers used to be local functions inside <c>Program.cs</c>'s top-level
/// statements, which made them unreachable from anywhere else: a command that wanted to own its
/// own argument rules (see <see cref="Commands.AnalyzeArgs"/>, where two mutually exclusive input
/// forms need validating and testing) would have had to write a second copy. They moved here
/// unchanged; <c>Program.cs</c> pulls them back in with a <c>using static</c>, so every call site
/// reads exactly as before.</para>
///
/// <para>Deliberately tiny and positional-free: a flag is <c>--name value</c> (or bare for a
/// switch), matched case-insensitively, and an unparseable value falls back to the default rather
/// than aborting the run.</para>
/// </summary>
internal static class CliArgs
{
    /// <summary>The value following <paramref name="flag"/>, or null when the flag is absent
    /// (or is the very last token, with no value after it).</summary>
    public static string? ParseStringFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>A positive integer flag value; anything else (absent, unparseable, &lt;= 0)
    /// yields <paramref name="defaultValue"/>.</summary>
    public static int ParseIntFlag(string[] args, string flag, int defaultValue)
    {
        var s = ParseStringFlag(args, flag);
        return s is not null && int.TryParse(s, out var v) && v > 0 ? v : defaultValue;
    }

    /// <summary>True when the switch is present anywhere in <paramref name="args"/>.</summary>
    public static bool ParseBoolFlag(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>A floating-point flag value, parsed invariantly (never locale-dependent, so a
    /// German machine reads "0.6" the same as an American one).</summary>
    public static double ParseDoubleFlag(string[] args, string flag, double defaultValue)
    {
        var s = ParseStringFlag(args, flag);
        return s is not null && double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v) ? v : defaultValue;
    }
}
