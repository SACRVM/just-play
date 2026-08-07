using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// How much of a track row the FIXED columns take - the numbers, the marks, the badges.
///
/// <para>It reads them out of <c>Theming/TrackTable.axaml</c>, the same <c>ColW*</c> resources the
/// cells themselves bind. That is the point: those widths already have ONE home, and copying them
/// into C# so the sizing maths could see them would create a second one that drifts the first time
/// someone widens a column in the XAML and not here.</para>
///
/// <para>What is left after these is what <see cref="TrackColumnWidths"/> shares out between the text
/// columns.</para>
/// </summary>
internal static class TrackTableMetrics
{
    /// <summary>Fixed column id -> the resource key holding its width. The FLEXIBLE text columns
    /// (title, artist, album, album artist, genre, comment, file name) are deliberately absent - they
    /// are sized from content, not from a constant.</summary>
    private static readonly (string Column, string Key)[] Fixed =
    [
        (TrackColumns.Year,     "ColWYear"),
        (TrackColumns.TrackNo,  "ColWTrackNo"),
        (TrackColumns.Bpm,      "ColWBpm"),
        (TrackColumns.Key,      "ColWKey"),
        (TrackColumns.Nrg,      "ColWNrg"),
        (TrackColumns.Gain,     "ColWGain"),
        (TrackColumns.Lufs,     "ColWLufs"),
        (TrackColumns.Dark,     "ColWVibe"),
        (TrackColumns.Hypnotic, "ColWVibe"),
        (TrackColumns.Groove,   "ColWVibe"),
        (TrackColumns.Punch,    "ColWVibe"),
        (TrackColumns.Harsh,    "ColWVibe"),
        (TrackColumns.Analysis, "ColWAnalysis"),
        (TrackColumns.Cover,    "ColWCover"),
        (TrackColumns.Id3,      "ColWId3"),
        (TrackColumns.FileType, "ColWFileType"),
        (TrackColumns.Duration, "ColWDuration"),
        (TrackColumns.Like,     "ColWLike"),
    ];

    private static readonly Dictionary<string, double> Cache = new(StringComparer.Ordinal);

    /// <summary>The total width of the fixed columns currently switched on, in px.</summary>
    public static double FixedTotal(TrackColumns columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var total = 0d;
        foreach (var (column, key) in Fixed)
            if (columns.IsEnabled(column))
                total += Lookup(key);

        return total;
    }

    private static double Lookup(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        // The dictionary is merged by the app at startup and never changes afterwards, so one lookup
        // per key for the process is enough. A missing key means the resource was renamed - fall back
        // to 0 rather than throwing: a slightly wrong column width is a cosmetic bug, an exception on
        // every row is not.
        var value = Application.Current?.TryFindResource(key, out var found) == true && found is double d
            ? d
            : 0d;

        Cache[key] = value;
        return value;
    }
}
