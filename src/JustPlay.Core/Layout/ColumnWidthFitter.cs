using System;
using System.Collections.Generic;
using System.Linq;

namespace JustPlay.Core.Layout;

/// <summary>One flexible column asking for space.</summary>
/// <param name="Key">Column id - the same string comes back in the result.</param>
/// <param name="Weight">
/// How much content this column typically holds. Any unit works, because only the RATIOS between
/// columns matter; in practice it is the 90th percentile of the cell's CHARACTER COUNT
/// (see <see cref="ColumnWidthFitter.Percentile"/>).
/// </param>
/// <param name="MinWidth">Never go below this, whatever the maths says.</param>
public readonly record struct ColumnDemand(string Key, double Weight, double MinWidth);

/// <summary>
/// Shares the space a track table has left over between its flexible TEXT columns.
///
/// <para><b>The idea</b> (Chloe 2026-08-06): look at what is actually IN a column and size it to the
/// 90th percentile of its content - the width 90 % of the rows fit into. The 10 % outliers, the one
/// track with the 140-character title, are deliberately ignored: sizing to the longest value lets a
/// single row dictate the whole table. Then the space that is left after the fixed-width columns
/// (BPM, KEY, TIME ...) is handed to the text columns IN PROPORTION to those percentile widths, so a
/// column whose content is typically three times longer gets three times the room.</para>
///
/// <para><b>Characters, not pixels</b>, on purpose: measuring real text is font- and DPI-dependent,
/// and since only the ratios are used, the pixels-per-character factor cancels out of the maths
/// entirely. It never has to be known or guessed.</para>
///
/// <para><b>Floors win.</b> A column that would be squeezed below its <see cref="ColumnDemand.MinWidth"/>
/// is pinned there and drops out of the split, and what it took is re-shared between the rest - the
/// standard constrained-proportional allocation, iterated until nothing else hits its floor. This is
/// what keeps the title column readable no matter how many columns are switched on ("never lose the
/// title", Chloe 2026-07-08 - the same 150 px rule TrackRow's Grid has always carried). If even the
/// floors do not fit, every column gets exactly its floor and the row's ClipToBounds does the rest,
/// which is the behaviour the table had before this existed.</para>
///
/// <para>Deliberately platform-agnostic (JustPlay.Core, no Avalonia): it is arithmetic, and it is the
/// part that has to be RIGHT, so it is unit-tested rather than eyeballed in a running window.</para>
/// </summary>
public static class ColumnWidthFitter
{
    /// <summary>
    /// Nearest-rank percentile of <paramref name="values"/> - the smallest value that at least
    /// <paramref name="p"/> of the sample is less than or equal to. p = 0.9 gives the width 90 % of
    /// the rows fit into. Returns 0 for an empty sample (a column with nothing in it asks for
    /// nothing, and its floor takes over).
    /// </summary>
    public static double Percentile(IEnumerable<int> values, double p = 0.9)
    {
        ArgumentNullException.ThrowIfNull(values);

        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;

        p = Math.Clamp(p, 0, 1);
        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// Split <paramref name="available"/> px between <paramref name="columns"/>, proportional to
    /// their weights and never below their floors. Every column in the input appears in the result.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Fit(
        double available, IReadOnlyList<ColumnDemand> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var result = new Dictionary<string, double>(columns.Count, StringComparer.Ordinal);
        if (columns.Count == 0) return result;

        // Not even the floors fit: pin everything and let the caller's clipping deal with it.
        var floorTotal = columns.Sum(c => Math.Max(0, c.MinWidth));
        if (!(available > floorTotal))
        {
            foreach (var c in columns) result[c.Key] = Math.Max(0, c.MinWidth);
            return result;
        }

        // A column with no content at all (empty tag across the whole view) still needs SOME weight,
        // or one non-empty column would swallow the entire table.
        var weights = columns.ToDictionary(
            c => c.Key, c => Math.Max(c.Weight, 1e-6), StringComparer.Ordinal);

        var pinned = new HashSet<string>(StringComparer.Ordinal);

        // Constrained proportional allocation: share out, pin whoever fell through their floor, then
        // re-share what is left between the rest. Only the WORST offender is pinned per pass -
        // pinning several at once over-corrects and can push a column that was fine below its own
        // floor. Each pass pins exactly one, so it ends after at most columns.Count passes.
        while (true)
        {
            var freeSpace = available - columns.Where(c => pinned.Contains(c.Key))
                                              .Sum(c => Math.Max(0, c.MinWidth));
            var freeWeight = columns.Where(c => !pinned.Contains(c.Key)).Sum(c => weights[c.Key]);
            if (freeWeight <= 0) break;

            string? worstKey = null;
            var worstDeficit = 0d;

            foreach (var c in columns)
            {
                if (pinned.Contains(c.Key)) continue;

                var share = freeSpace * weights[c.Key] / freeWeight;
                var deficit = c.MinWidth - share;
                if (deficit > 0 && (worstKey is null || deficit > worstDeficit))
                {
                    worstKey = c.Key;
                    worstDeficit = deficit;
                }
            }

            if (worstKey is null)
            {
                // Everyone clears their floor - this share-out is the answer.
                foreach (var c in columns)
                {
                    if (pinned.Contains(c.Key)) continue;
                    result[c.Key] = freeSpace * weights[c.Key] / freeWeight;
                }
                break;
            }

            pinned.Add(worstKey);
        }

        foreach (var c in columns)
            if (pinned.Contains(c.Key)) result[c.Key] = Math.Max(0, c.MinWidth);

        return result;
    }
}
