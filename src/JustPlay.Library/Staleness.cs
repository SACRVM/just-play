using System.Globalization;

namespace JustPlay.Library;

/// <summary>
/// One reason an existing index entry should be re-analysed even though the FILE has not changed.
///
/// <para>Why a rule and not a version number: measured 2026-07-30, all 6,561 entries of the "v9"
/// library index carry <c>detectionVersion: 1</c> - a single scalar cannot express what actually
/// needs redoing. The FLAC mono-decode bug (fixed in c687d46, 2026-07-10) corrupted vibe/LUFS/
/// rhythm on FLACs while keys and BPM stayed correct; bumping a global version would have
/// re-analysed thousands of healthy MP3s to fix a few hundred FLACs. Rules are recombinable,
/// which is both smaller and more honest than a version matrix.</para>
/// </summary>
/// <param name="Reason">Short, user-facing reason - shown in the library panel's stale count.</param>
/// <param name="Matches">True when this rule considers the entry stale.</param>
public sealed record StaleRule(string Reason, Func<TrackIndexEntry, bool> Matches)
{
    // -- Rule factories --------------------------------------------------------

    /// <summary>
    /// Entries whose analysis FAILED - worth retrying (a locked or half-copied file).
    /// Deliberately not the same as never-analysed: a panel that reports "412 failed" when those
    /// files were simply never processed sends the reader hunting for a bug that is not there.
    /// </summary>
    public static StaleRule Failed() =>
        new("analysis failed", e => !e.Success && e.Error is not null);

    /// <summary>Entries the sync knows about but that have never been analysed.</summary>
    public static StaleRule NeverAnalysed() =>
        new("never analysed", e => e.NeedsAnalysis);

    /// <summary>Entries written by a detection stack older than <paramref name="version"/>.</summary>
    public static StaleRule DetectionVersionBelow(int version) =>
        new($"detection version < {version}", e => e.DetectionVersion < version);

    /// <summary>Entries missing a value the current stack always produces (e.g. grid confidence).</summary>
    public static StaleRule MissingValue(string label, Func<TrackIndexEntry, bool> isMissing) =>
        new($"missing {label}", isMissing);

    /// <summary>
    /// Entries analysed before <paramref name="cutoffUtc"/> whose file has one of
    /// <paramref name="extensions"/>. Two independent "we don't actually know" cases both err
    /// toward re-analysis, deliberately - a wrong "needs redoing" costs DSP time, a wrong "clean"
    /// ships bad numbers forever:
    /// <list type="bullet">
    /// <item>An unparsable <c>AnalysedAt</c> (garbage/foreign data) counts as old.</item>
    /// <item>
    /// <see cref="TrackIndexEntry.UnknownAnalysedAt"/> (the sentinel <see cref="TrackIndexMapping.FromStoredBlob"/>
    /// stamps when a blob carries no timestamp of its own) parses FINE and is simply always
    /// earlier than any real-world <paramref name="cutoffUtc"/> - no special-casing needed here,
    /// it falls straight through the normal comparison below. Measured 2026-08-01 (night report
    /// L6): before that sentinel existed, this path stamped the IMPORT moment instead, which made
    /// every rule built on this helper blind on any row that came from a tag import (i.e. most of
    /// the library) - it always looked freshly analysed. See
    /// <see cref="TrackIndexEntry.UnknownAnalysedAt"/> for the full reasoning.
    /// </item>
    /// </list>
    /// </summary>
    public static StaleRule AnalysedBefore(string reason, DateTime cutoffUtc, params string[] extensions) =>
        new(reason, e =>
        {
            if (extensions.Length > 0 &&
                !extensions.Contains(Path.GetExtension(e.FilePath), StringComparer.OrdinalIgnoreCase))
                return false;

            if (!DateTime.TryParse(e.AnalysedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var analysed))
                return true;

            return analysed.ToUniversalTime() < cutoffUtc;
        });

    /// <summary>
    /// (!) The FLAC/WAV/AIFF mono-decode debt. <c>DecodeMono</c> handed BASS's interleaved stereo
    /// back as "mono" until commit c687d46 (2026-07-10), which shifted vibe quartet, LUFS and
    /// rhythm features on every file whose BASS decoder plugin ignores the mono flag (keys and BPM
    /// were unaffected - see that commit's message). That is FLAC, WAV and AIFF/AIF, not just
    /// FLAC: measured 2026-08-01 (night report L6), the rule scoped to <c>.flac</c> alone missed
    /// ~473 further WAV/AIFF/AIF files - AIFF alone is 23% of the library, so this was not a
    /// rounding-error gap.
    ///
    /// <para>Our pipeline re-runs all detectors, so this re-measures key/BPM too - wasteful but
    /// correct.</para>
    ///
    /// <para><b>(!!) It does NOT find the existing debt, and it is not the mechanism that pays it
    /// off.</b> Measured 2026-08-07: against the real 18,009-row index this rule reports <b>0</b>,
    /// which is a FALSE clean. The rows were stamped with their IMPORT moment, not with the moment
    /// the DSP ran, because the blob carried no timestamp of its own back then; the import happened
    /// AFTER the cutoff, so every affected row reads as freshly analysed. The
    /// <see cref="TrackIndexEntry.UnknownAnalysedAt"/> sentinel that makes "we don't know" read as
    /// stale arrived later - and a normal re-sync can never deliver it to these rows, because
    /// <c>LibrarySync</c> skips a file whose size and mtime are unchanged, which is exactly what
    /// these files are. So this rule is sound for rows indexed from the sentinel onward and blind
    /// on every row imported before it.</para>
    ///
    /// <para>The 957 affected paths were therefore enumerated once, by hand, and a batch is pointed
    /// at that LIST rather than at this rule. Do not read a 0 here as "the debt is paid".</para>
    ///
    /// <para>The cause underneath is worth naming, because it is general: c687d46 changed what the
    /// analyzers COMPUTE without bumping <c>TrackAnalysisState.CurrentVersion</c>. Detection version
    /// is the one field that survives a sync skip, so it is the only thing that could have told the
    /// two v9s apart - and it was left identical. An analyzer golden test now pins that invariant,
    /// so an output change fails the build instead of going unnoticed for a month.</para>
    /// </summary>
    public static StaleRule FlacMonoDecodeBug() =>
        AnalysedBefore(
            "FLAC/WAV/AIFF mono-decode fix (c687d46)",
            new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            ".flac", ".wav", ".aiff", ".aif");
}

/// <summary>
/// The set of <see cref="StaleRule"/>s a scan applies. Empty = trust every existing entry, which
/// is the library's default posture: a stale blob is trusted as-is and never silently re-analysed
/// (analysis-tag-persistence design). Re-analysis is something a rule - or an explicit request -
/// asks for.
/// </summary>
public sealed class StalenessPolicy
{
    private readonly List<StaleRule> _rules = [];

    /// <summary>Trust everything already in the index.</summary>
    public static StalenessPolicy TrustEverything => new();

    /// <summary>The rules this policy applies, in evaluation order.</summary>
    public IReadOnlyList<StaleRule> Rules => _rules;

    public StalenessPolicy With(StaleRule rule)
    {
        _rules.Add(rule);
        return this;
    }

    /// <summary>
    /// The first reason <paramref name="entry"/> is stale, or null if it is trustworthy.
    /// Returning the REASON (not just a bool) is what lets the library panel say
    /// "412 stale - FLAC mono-decode fix" instead of an unexplained number.
    /// </summary>
    public string? StaleReason(TrackIndexEntry entry)
    {
        foreach (var rule in _rules)
            if (rule.Matches(entry))
                return rule.Reason;
        return null;
    }

    /// <summary>Groups the stale entries of <paramref name="index"/> by reason - the panel's counts.</summary>
    public IReadOnlyDictionary<string, int> CountByReason(TrackIndex index)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in index.Entries.Values)
        {
            var reason = StaleReason(entry);
            if (reason is null) continue;
            counts[reason] = counts.TryGetValue(reason, out var n) ? n + 1 : 1;
        }
        return counts;
    }
}
