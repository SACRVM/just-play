using System;
using System.Collections.Generic;

namespace JustPlay.UI.ViewModels;

/// <summary>Explorer-style natural/logical string ordering: compares digit runs by numeric value
/// ("track2" &lt; "track10") and the rest case-insensitively. Managed (no P/Invoke) so it stays
/// portable and trim/AOT-safe. Shared by the queue (sorting, folder drops), the PRE CUE FINDER
/// listing and JUST TAG - it was nested in MainWindowViewModel until the finder needed it too, and
/// moved here with <see cref="TrackSort"/> when a third app started sorting the same rows.</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;   // longer number = larger
                var cmp = na.CompareTo(nb, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }
}
