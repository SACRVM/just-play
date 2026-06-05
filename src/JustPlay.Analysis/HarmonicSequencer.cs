namespace JustPlay.Analysis;

/// <summary>
/// Greedily re-orders a queue of tracks to maximise the sum of pairwise
/// <see cref="MixCompatibility.Score"/> scores on consecutive pairs, then refines
/// the path with 2-opt swaps.
///
/// <para><b>Algorithm (O(n²)):</b></para>
/// <list type="number">
///   <item>
///     <b>Partition:</b> separate tracks into "scoreable" (have at least one of
///     Key, Bpm, Energy) and "unanalyzed" (all three null).  Unanalyzed tracks are
///     appended at the END in their original relative order — they cannot contribute
///     to a meaningful compatibility score so they must not perturb the sequenced
///     block. The count is reported via <see cref="HarmonicSequenceResult.UnanalyzedCount"/>.
///   </item>
///   <item>
///     <b>Greedy nearest-neighbour:</b> start from the pinned track (index 0 of the
///     scoreable segment, which is the caller's chosen anchor — typically the currently
///     playing track). On each step, pick the unused track with the highest
///     <c>CompatResult.Combined</c> to the last placed track. Ties are broken by the
///     original position (stable).
///   </item>
///   <item>
///     <b>2-opt improvement:</b> iterate over all pairs of non-adjacent edges in the
///     path and reverse the segment between them if doing so increases the total sum of
///     consecutive compatibility scores. Repeat until no improving swap exists. The
///     start track is <em>pinned</em> and excluded from any reversal that would move it
///     away from position 0.
///   </item>
/// </list>
///
/// <para><b>All four axes active (v4 blobs):</b></para>
/// <see cref="MixCompatibility.Score"/> uses Beat (0.40), Tempo (0.30), Harmonic (0.20),
/// and Energy (0.10) when all data is present.  Tracks analysed before blob v4 (or tracks
/// too short for the fingerprint extractor) will have <c>Fingerprint = null</c>;
/// <see cref="MixCompatibility"/> degrades gracefully — the Beat axis is excluded and
/// the remaining weights are renormalised.
/// Planned next iteration:
/// <list type="bullet">
///   <item>Anti-monotony / energy-arc shaping: bias the 2-opt cost to gently vary the
///         energy level so the set breathes ("don't make it monotonous" rule).</item>
/// </list>
///
/// <para>
/// Pure / deterministic — no Avalonia, no ManagedBass, no NuGet, reflection-free,
/// trim-/AOT-safe.  Part of the north-star "sort by harmony" roadmap.
/// </para>
/// </summary>
public static class HarmonicSequencer
{
    /// <summary>
    /// Sequence a list of tracks for optimal mix-compatibility.
    /// </summary>
    /// <param name="tracks">
    /// Input features.  The caller must pass one entry per queue track, in the
    /// same order as the queue.  <c>null</c> entries (or entries with all-null
    /// fields) are treated as unanalyzed and placed at the end.
    /// </param>
    /// <param name="startIndex">
    /// Index into <paramref name="tracks"/> of the track that must stay first in
    /// the result (typically the currently-playing track).  Pass -1 or out-of-range
    /// to let the algorithm pick its own best start.
    /// </param>
    /// <returns>
    /// A <see cref="HarmonicSequenceResult"/> whose <c>Order</c> is the permutation
    /// (each element is an original index into <paramref name="tracks"/>).
    /// </returns>
    public static HarmonicSequenceResult Sequence(
        IReadOnlyList<TrackFeatures?> tracks,
        int startIndex = -1)
    {
        if (tracks.Count == 0)
            return new HarmonicSequenceResult([], 0);

        // ── 1. Partition into scoreable vs unanalyzed ───────────────────────
        // A track is "scoreable" if it has at least one of Bpm / Key / Energy.
        var scoreable   = new List<int>();  // original indices, in original order
        var unanalyzed  = new List<int>();  // original indices, in original order

        for (var i = 0; i < tracks.Count; i++)
        {
            var f = tracks[i];
            if (f is not null && (f.Bpm is not null || f.Key is not null || f.Energy is not null))
                scoreable.Add(i);
            else
                unanalyzed.Add(i);
        }

        // Short-circuit: nothing scoreable — return original order.
        if (scoreable.Count == 0)
        {
            var trivial = Enumerable.Range(0, tracks.Count).ToArray();
            return new HarmonicSequenceResult(trivial, unanalyzed.Count);
        }

        // ── 2. Determine the pinned start within the scoreable segment ───────
        // If startIndex points at a scoreable track, pin it first.
        // Otherwise (unanalyzed, out-of-range, -1), let greedy pick its own start.
        int pinnedLocal = -1; // index within `scoreable`
        if (startIndex >= 0 && startIndex < tracks.Count)
        {
            var posInScoreable = scoreable.IndexOf(startIndex);
            if (posInScoreable >= 0) pinnedLocal = posInScoreable;
        }

        // ── 3. Pre-compute the full pairwise compatibility matrix ────────────
        // O(n²) comparisons, but queues are small (tens of tracks).
        var n = scoreable.Count;
        var compat = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            var fi = tracks[scoreable[i]]!;
            for (var j = i + 1; j < n; j++)
            {
                var fj = tracks[scoreable[j]]!;
                var score = MixCompatibility.Score(fi, fj).Combined;
                compat[i, j] = score;
                compat[j, i] = score;
            }
        }

        // ── 4. Greedy nearest-neighbour ─────────────────────────────────────
        var path    = new int[n];  // local indices (into `scoreable`)
        var used    = new bool[n];
        int cursor;

        if (pinnedLocal >= 0)
        {
            path[0] = pinnedLocal;
            used[pinnedLocal] = true;
            cursor = 0;
        }
        else
        {
            // Pick the best overall start (highest sum of top-two neighbours).
            // Simple heuristic: start at the track with the highest average compat.
            var bestStart = 0;
            var bestAvg   = -1.0;
            for (var i = 0; i < n; i++)
            {
                var sum = 0.0;
                for (var j = 0; j < n; j++) sum += compat[i, j];
                var avg = n > 1 ? sum / (n - 1) : 0.0;
                if (avg > bestAvg) { bestAvg = avg; bestStart = i; }
            }
            path[0] = bestStart;
            used[bestStart] = true;
            cursor = 0;
        }

        for (var step = 1; step < n; step++)
        {
            var last      = path[step - 1];
            var bestNext  = -1;
            var bestScore = -1.0;
            for (var j = 0; j < n; j++)
            {
                if (used[j]) continue;
                if (compat[last, j] > bestScore)
                {
                    bestScore = compat[last, j];
                    bestNext  = j;
                }
            }
            path[step] = bestNext;
            used[bestNext] = true;
            cursor = step;
        }

        // ── 5. 2-opt improvement ─────────────────────────────────────────────
        // Pin position 0 (the anchor track must stay first).
        // Reverse the segment path[i..j] if it improves the total path cost.
        // The reversed segment must not include index 0 — so i >= 1.
        bool improved = true;
        while (improved)
        {
            improved = false;
            for (var i = 1; i < n - 1; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    // Current cost: edge (i-1, i) + edge (j, j+1) [if j+1 exists]
                    var costBefore =
                        compat[path[i - 1], path[i]] +
                        (j + 1 < n ? compat[path[j], path[j + 1]] : 0.0);

                    // Proposed cost after reversing segment [i..j]:
                    // edge (i-1, j) + edge (i, j+1) [if j+1 exists]
                    var costAfter =
                        compat[path[i - 1], path[j]] +
                        (j + 1 < n ? compat[path[i], path[j + 1]] : 0.0);

                    if (costAfter > costBefore + 1e-10)
                    {
                        // Reverse the sub-path [i..j]
                        var lo = i;
                        var hi = j;
                        while (lo < hi)
                        {
                            (path[lo], path[hi]) = (path[hi], path[lo]);
                            lo++;
                            hi--;
                        }
                        improved = true;
                    }
                }
            }
        }

        // ── 6. Assemble final permutation (original indices) ─────────────────
        // Scoreable tracks in optimised order, then unanalyzed in original order.
        var order = new int[tracks.Count];
        for (var i = 0; i < n; i++)
            order[i] = scoreable[path[i]];
        for (var i = 0; i < unanalyzed.Count; i++)
            order[n + i] = unanalyzed[i];

        return new HarmonicSequenceResult(order, unanalyzed.Count);
    }
}

/// <summary>
/// Result of <see cref="HarmonicSequencer.Sequence"/>.
/// </summary>
/// <param name="Order">
/// Permutation array of length N. Each element is an original index into the
/// input <c>tracks</c> list. Apply this order by doing
/// <c>for (var i = 0; i &lt; Order.Length; i++) newQueue[i] = oldQueue[Order[i]];</c>
/// </param>
/// <param name="UnanalyzedCount">
/// Number of tracks at the END of <see cref="Order"/> that could not be scored
/// (no Key, BPM, or Energy). They retain their original relative order.
/// </param>
public sealed record HarmonicSequenceResult(int[] Order, int UnanalyzedCount);
