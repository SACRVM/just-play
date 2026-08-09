using System.Globalization;

namespace JustPlay.Core;

/// <summary>
/// A byte count in the words a person reads. One formatter for the whole suite, so a scan report,
/// an organise preview and a library stat line can never disagree about how big the same folder is.
///
/// <para>Binary units (1024), which is what every file manager on every platform this suite runs on
/// shows.</para>
///
/// <para>(!) Formatted with the INVARIANT culture, explicitly. Every shipping project already sets
/// <c>InvariantGlobalization=true</c>, so this is what the apps do anyway - but a shared formatter
/// that silently followed the machine's culture would produce "1,0 MB" in one host and "1.0 MB" in
/// another, and the first place that shows up is a test run.</para>
/// </summary>
public static class ByteSize
{
    private const double Kb = 1024d;
    private const double Mb = Kb * 1024d;
    private const double Gb = Mb * 1024d;

    /// <summary>Format a byte count as B / KB / MB / GB.</summary>
    public static string Format(long bytes)
    {
        var c = CultureInfo.InvariantCulture;
        return bytes switch
        {
            < 1024L                 => bytes.ToString(c) + " B",
            < 1024L * 1024L         => (bytes / Kb).ToString("F1", c) + " KB",
            < 1024L * 1024L * 1024L => (bytes / Mb).ToString("F1", c) + " MB",
            _                       => (bytes / Gb).ToString("F2", c) + " GB",
        };
    }

    /// <summary>A plain count with thousands separators - "1,203". Same invariant rule.</summary>
    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
