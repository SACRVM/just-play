using System.Text;

namespace JustPlay.Analysis;

/// <summary>
/// <b>Squeeze</b> — compress a messy pool of tracks down to the <c>keep</c> tracks that mix
/// best together (the densest mutually-compatible core), dropping the outliers and reporting
/// HONESTLY when fewer than <c>keep</c> tracks are truly coherent.
///
/// <para><b>One compat core:</b> Squeeze is the third query shape over the SAME pairwise
/// <see cref="MixCompatibility.Score"/> that powers Sort+ (<see cref="HarmonicSequencer"/>) and
/// the planned Match feature. It never duplicates the scoring or hard-codes weights — retuning
/// <see cref="MixCompatibility"/> automatically improves Squeeze. See the design spec
/// <c>.claude/skills/dj-audio-analysis/references/harmonic-sort-design.md</c>.</para>
///
/// <para><b>Algorithm (greedy cohesion):</b></para>
/// <list type="number">
///   <item><b>Partition</b> into <i>scoreable</i> (≥1 of BPM/Key/Energy) and <i>unanalyzed</i>.
///     Unanalysable tracks can't be assessed for compatibility, so they are ALWAYS dropped — and
///     counted (<see cref="SqueezeResult.UnanalyzedDropped"/>), never silently lost.</item>
///   <item><b>Build</b> the symmetric pairwise compat matrix over the scoreable set via
///     <see cref="MixCompatibility.Score"/>.</item>
///   <item><b>Seed</b> = the most "central" track = the one with the highest mean compat to the
///     rest of the pool (the densest hub; same idea as <see cref="HarmonicSequencer"/>'s best-start
///     picker).</item>
///   <item><b>Grow</b> the core greedily: repeatedly add the remaining track that maximises its
///     mean compat to the current core, until the core reaches <c>min(keep, n)</c>. Each added
///     track's mean compat to the core-at-join is its <c>joinCohesion</c>.</item>
///   <item><b>Coherent prefix</b> = the longest run from the seed (in growth order) whose every
///     joinCohesion ≥ <paramref name="coherenceThreshold"/> (the seed counts as 1). Because greedy
///     adds the best remaining track each step, joinCohesion trends down — the first drop below the
///     threshold marks where the set stops being genuinely coherent.</item>
///   <item><b>Honest report:</b> <see cref="SqueezeResult.EnoughCoherent"/> is false when fewer
///     than <c>keep</c> tracks clear the threshold (or when there aren't even <c>keep</c> scoreable
///     tracks); the message says "only X of N form a coherent set — loosen or accept the break?".</item>
/// </list>
///
/// <para>The kept set is optionally re-ordered into a play-ready sequence via
/// <see cref="HarmonicSequencer"/>. Squeeze always returns its best-effort N (a usable set) but
/// makes it loud when N is a stretch.</para>
///
/// <para>Pure / deterministic — no Avalonia, no ManagedBass, no NuGet, reflection-free,
/// trim-/AOT-safe. Part of the north-star "sort by harmony" roadmap (Phase 2).</para>
/// </summary>
public static class SetSqueezer
{
    /// <summary>
    /// Default coherence threshold. With the live <see cref="MixCompatibility"/> model a tight
    /// neighbourhood (same Camelot area + BPM) scores ~0.9+ while a clashing-key /
    /// un-beatmatchable pair scores &lt; 0.1, so 0.60 cleanly separates "mixes" from "doesn't".
    /// </summary>
    public const double DefaultCoherenceThreshold = 0.60;

    /// <summary>
    /// Squeeze <paramref name="pool"/> down to the <paramref name="keep"/> mutually-compatible
    /// tracks. See the class summary for the algorithm.
    /// </summary>
    /// <param name="pool">Candidate tracks. <c>null</c> entries are treated as unanalyzed.</param>
    /// <param name="keep">Target kept-set size N (best-effort; clamped to the scoreable count).</param>
    /// <param name="coherenceThreshold">Min compat for a track to count as part of the coherent
    /// core (default <see cref="DefaultCoherenceThreshold"/>).</param>
    /// <param name="sequence">When true (default) the kept set is re-ordered for the best play
    /// sequence via <see cref="HarmonicSequencer"/>; when false the greedy growth order is kept.</param>
    public static SqueezeResult Squeeze(
        IReadOnlyList<TrackFeatures?> pool,
        int keep,
        double coherenceThreshold = DefaultCoherenceThreshold,
        bool sequence = true)
    {
        if (pool.Count == 0)
            return new SqueezeResult([], [], -1, 1.0, 1.0, 0, keep, true,
                "Empty pool — nothing to squeeze.", 0);

        // ── 1. Partition scoreable vs unanalyzed ────────────────────────────────
        var scoreable  = new List<int>();
        var unanalyzed = new List<int>();
        for (var i = 0; i < pool.Count; i++)
        {
            var f = pool[i];
            if (f is not null && (f.Bpm is not null || f.Key is not null || f.Energy is not null))
                scoreable.Add(i);
            else
                unanalyzed.Add(i);
        }

        var n = scoreable.Count;
        if (n == 0)
        {
            // Nothing can be scored → everything is an (honest) drop.
            return new SqueezeResult(
                [], unanalyzed.ToArray(), -1, 0.0, 0.0, 0, keep, false,
                $"None of the {pool.Count} track(s) have BPM/Key/Energy — cannot assess " +
                "compatibility. All dropped (need analysis first).",
                unanalyzed.Count);
        }

        // ── 2. Pairwise compat matrix over the scoreable set ────────────────────
        var compat = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            var fi = pool[scoreable[i]]!;
            for (var j = i + 1; j < n; j++)
            {
                var fj = pool[scoreable[j]]!;
                var s = MixCompatibility.Score(fi, fj).Combined;
                compat[i, j] = s;
                compat[j, i] = s;
            }
        }

        // ── 3. Most central seed = highest mean compat to the rest of the pool ──
        var seedLocal = 0;
        var bestMean  = -1.0;
        for (var i = 0; i < n; i++)
        {
            var sum = 0.0;
            for (var j = 0; j < n; j++) if (j != i) sum += compat[i, j];
            var mean = n > 1 ? sum / (n - 1) : 1.0;
            if (mean > bestMean) { bestMean = mean; seedLocal = i; }
        }

        var effectiveKeep = Math.Clamp(keep, 0, n);
        if (effectiveKeep == 0)
        {
            // Caller asked to keep nothing — drop the lot (still honest, no data lost).
            var dropAll = scoreable.Concat(unanalyzed).ToArray();
            return new SqueezeResult([], dropAll, scoreable[seedLocal], 1.0, 1.0, 0, keep, keep <= 0,
                "keep ≤ 0 — kept nothing.", unanalyzed.Count);
        }

        // ── 4. Greedily grow the cohesive core from the seed ────────────────────
        var inCore       = new bool[n];
        var coreOrder    = new List<int>(effectiveKeep);     // local indices, seed-first
        var joinCohesion = new List<double>(effectiveKeep);  // mean compat to core-at-join

        coreOrder.Add(seedLocal);
        inCore[seedLocal] = true;
        joinCohesion.Add(1.0);   // the seed anchors the set — fully coherent by definition

        while (coreOrder.Count < effectiveKeep)
        {
            var bestCand     = -1;
            var bestCandMean = -1.0;
            for (var j = 0; j < n; j++)
            {
                if (inCore[j]) continue;
                var sum = 0.0;
                foreach (var c in coreOrder) sum += compat[j, c];
                var mean = sum / coreOrder.Count;
                if (mean > bestCandMean) { bestCandMean = mean; bestCand = j; }
            }
            if (bestCand < 0) break;   // no candidates left (shouldn't happen — guarded by effectiveKeep)
            coreOrder.Add(bestCand);
            inCore[bestCand] = true;
            joinCohesion.Add(bestCandMean);
        }

        // ── 5. Cohesion stats over the final core ───────────────────────────────
        var (meanCohesion, minCohesion) = CoreCohesion(compat, coreOrder);

        // ── 6. Coherent prefix (run from the seed clearing the threshold) ───────
        var coherentCount = 1;   // the seed always counts
        for (var k = 1; k < coreOrder.Count; k++)
        {
            if (joinCohesion[k] >= coherenceThreshold) coherentCount++;
            else break;
        }

        // ── 7. Assemble kept (optionally sequenced) + dropped ───────────────────
        int[] keptIndices;
        if (sequence && coreOrder.Count > 1)
        {
            var keptFeatures = new TrackFeatures?[coreOrder.Count];
            for (var k = 0; k < coreOrder.Count; k++)
                keptFeatures[k] = pool[scoreable[coreOrder[k]]];

            var seq = HarmonicSequencer.Sequence(keptFeatures);   // -1 → best auto-start
            keptIndices = new int[coreOrder.Count];
            for (var k = 0; k < seq.Order.Length; k++)
                keptIndices[k] = scoreable[coreOrder[seq.Order[k]]];
        }
        else
        {
            keptIndices = new int[coreOrder.Count];
            for (var k = 0; k < coreOrder.Count; k++)
                keptIndices[k] = scoreable[coreOrder[k]];
        }

        var dropped = new List<int>();
        for (var j = 0; j < n; j++) if (!inCore[j]) dropped.Add(scoreable[j]);
        dropped.AddRange(unanalyzed);

        // ── 8. Honest report ────────────────────────────────────────────────────
        var enough  = coherentCount >= keep;   // (coherentCount ≤ min(keep,n); >keep impossible)
        var message = BuildMessage(
            keep, n, coreOrder.Count, coherentCount,
            meanCohesion, minCohesion, coherenceThreshold, unanalyzed.Count);

        return new SqueezeResult(
            keptIndices, dropped.ToArray(), scoreable[seedLocal],
            meanCohesion, minCohesion, coherentCount, keep, enough, message, unanalyzed.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Mean and minimum of all pairwise compat scores within the core. A single-element
    /// (or empty) core is defined as fully cohesive (1.0, 1.0).</summary>
    private static (double mean, double min) CoreCohesion(double[,] compat, List<int> core)
    {
        if (core.Count <= 1) return (1.0, 1.0);
        var sum = 0.0;
        var count = 0;
        var min = double.MaxValue;
        for (var a = 0; a < core.Count; a++)
        for (var b = a + 1; b < core.Count; b++)
        {
            var s = compat[core[a], core[b]];
            sum += s;
            count++;
            if (s < min) min = s;
        }
        return (count > 0 ? sum / count : 1.0, min is double.MaxValue ? 1.0 : min);
    }

    private static string BuildMessage(
        int requested, int scoreableCount, int keptCount, int coherentCount,
        double mean, double min, double threshold, int unanalyzedDropped)
    {
        var sb = new StringBuilder();

        if (requested > scoreableCount)
            sb.Append($"Asked to keep {requested} but only {scoreableCount} track(s) are analysable; kept {keptCount}. ");

        if (coherentCount >= requested && requested <= scoreableCount)
            sb.Append($"All {keptCount} kept track(s) form a coherent set " +
                      $"(mean cohesion {mean:0.00}, weakest pair {min:0.00}). ");
        else
            sb.Append($"Only {coherentCount} of the {requested} requested form a coherent set; " +
                      $"the last {Math.Max(0, keptCount - coherentCount)} would be a stretch " +
                      $"(mean cohesion {mean:0.00}, weakest pair {min:0.00}, threshold {threshold:0.00}). " +
                      "Loosen the criteria or accept the break? ");

        if (unanalyzedDropped > 0)
            sb.Append($"{unanalyzedDropped} unanalysed track(s) dropped (no BPM/Key/Energy). ");

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Result of <see cref="SetSqueezer.Squeeze"/>. All index lists are <b>original</b> indices into
/// the input pool, so the caller maps them straight back to its own track list.
/// </summary>
/// <param name="KeptIndices">The kept core (size <c>min(keep, scoreable)</c>), in play order when
/// sequencing was requested, else in greedy growth order (seed first).</param>
/// <param name="DroppedIndices">Outliers + all unanalysable tracks. Kept ∪ Dropped = the whole pool.</param>
/// <param name="SeedIndex">Original index of the most-central seed track (-1 if none scoreable).</param>
/// <param name="MeanCohesion">Mean of all pairwise compat scores within the kept core ∈ [0,1].</param>
/// <param name="MinCohesion">Weakest pairwise compat within the kept core ∈ [0,1].</param>
/// <param name="CoherentCount">How many kept tracks form a genuinely coherent run (≥ threshold).</param>
/// <param name="RequestedKeep">The N the caller asked for (may exceed what was achievable).</param>
/// <param name="EnoughCoherent">False when fewer than <see cref="RequestedKeep"/> tracks are coherent
/// (or fewer than N scoreable tracks exist) — the honest "this is a stretch" flag.</param>
/// <param name="Message">Human-readable summary of the squeeze (cohesion + honesty note).</param>
/// <param name="UnanalyzedDropped">Count of tracks dropped because they had no BPM/Key/Energy.</param>
public sealed record SqueezeResult(
    IReadOnlyList<int> KeptIndices,
    IReadOnlyList<int> DroppedIndices,
    int                SeedIndex,
    double             MeanCohesion,
    double             MinCohesion,
    int                CoherentCount,
    int                RequestedKeep,
    bool               EnoughCoherent,
    string             Message,
    int                UnanalyzedDropped);
