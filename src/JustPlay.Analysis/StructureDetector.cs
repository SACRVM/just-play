namespace JustPlay.Analysis;

/// <summary>
/// A detected structural boundary within a track - an estimated timestamp where
/// the music transitions between sections (intro, build-up, drop, breakdown, outro, ...).
/// </summary>
/// <param name="TimeSeconds">
/// Estimated boundary position in seconds from the start of the track.
/// </param>
/// <param name="NoveltyScore">
/// Novelty value at this peak (higher = more abrupt/confident transition).
/// Normalised to [0, 1] within the track.
/// </param>
public readonly record struct StructureBoundary(double TimeSeconds, double NoveltyScore);

/// <summary>
/// Pure-DSP structural segmentation using the Foote (2000) Self-Similarity Matrix +
/// Gaussian-checkerboard novelty curve, tuned for EDM sections (drop / breakdown /
/// intro / outro) where boundaries are defined by energy/timbre/rhythmic changes,
/// NOT harmonic content.
///
/// <para>Pipeline (all on the existing hand-rolled radix-2 FFT, no NuGet, no ML):</para>
/// <list type="number">
///   <item>
///     <b>Frame feature vectors</b> at ~2 fps (512-sample hop at 11 025 Hz ~ 46 ms,
///     then optionally pooled to a coarser novelty grid).
///     Per frame: 4 sub-band energies (sub-bass <= 80 Hz, bass 80-250 Hz,
///     mid 250-2 kHz, hi 2-6 kHz) + spectral centroid + spectral flatness +
///     rectified spectral flux (onset density proxy). 7-D feature vector,
///     L2-normalised so cosine similarity is a dot product.
///   </item>
///   <item>
///     <b>Self-Similarity Matrix</b> S[i,j] = cos(f_i, f_j).
///     Homogeneous sections appear as block-diagonal plateaux; transitions as
///     checkerboard patterns at block corners.
///   </item>
///   <item>
///     <b>Gaussian-tapered checkerboard kernel</b> convolved along the main diagonal
///     of S. The kernel has +1 in the top-left and bottom-right quadrants, -1 in
///     the top-right and bottom-left (the "checkerboard"), tapered by a 2D isotropic
///     Gaussian to give smooth, localised detection.
///     [Foote ICME 2000; Mueller FMP Sec.4.4]
///   </item>
///   <item>
///     <b>Peak-picking</b>: local maxima above a mean + k-sigma adaptive threshold.
///     Min inter-peak gap is <c>MinBoundaryGapSeconds</c> (default 8 s) - structure
///     sections shorter than this are not individually labelled.
///   </item>
///   <item>
///     <b>Optional beatgrid snap</b>: when <paramref name="bpm"/> is supplied the
///     detected boundary is snapped to the nearest 4-bar phrase boundary within a
///     +/-2-bar window. This is the canonical DJ-phrasing correction described in
///     [structure-detection.md Sec.Foote SSM, bpm-detection.md Sec.downbeat].
///   </item>
/// </list>
///
/// <para><b>Calibration notes:</b>
/// The feature vector and kernel half-size defaults are designed for EDM tracks at
/// 11 025 Hz (same rate used by SpectralEnergyDetector / ChromagramKeyDetector).
/// They have NOT been exhaustively validated on a benchmark dataset. The unit tests
/// use a synthetic two-section signal (low-energy -> high-energy) where a boundary
/// must be found near the transition; this is a necessary-but-not-sufficient
/// verification. Real-track validation is left for a follow-up day session.
/// </para>
///
/// <para>Platform-agnostic: no Avalonia, no ManagedBass, no NuGet. Trim/AOT-safe.</para>
///
/// References:
/// [structure-detection.md Sec.Lightweight structure detection - Foote (2000)]
/// [structure-detection.md Sec.EDM structure is ENERGY/TIMBRE/RHYTHM]
/// </summary>
public sealed class StructureDetector
{
    // -------------------------------------------------------------------------
    // Frame / hop configuration
    // -------------------------------------------------------------------------

    // FFT frame size for spectral features.  Must be a power of two.
    // 2048 samples @ 11 025 Hz ~ 186 ms - matches SpectralEnergyDetector.
    private const int FrameSize = 2048;

    // Hop between frames.  512 samples @ 11 025 Hz ~ 46 ms.
    // Using a 4x overlap keeps the frame-level novelty curve at ~22 fps before
    // pooling.  We then pool into coarser "structure frames" for the SSM.
    private const int HopSize = 512;

    // Number of fine FFT frames to pool into one SSM row (mean-pooled energy/flux).
    // 22 fine frames per second x 11 ~ ~2 SSM frames per second (~500 ms per cell).
    // At 2 fps a 4-minute track -> ~480 SSM cells - a 480x480 matrix is cheap (~900 KB float).
    private const int PoolFrames = 11;

    // Gaussian-checkerboard kernel half-size in SSM cells.
    // At ~2 fps, a 16-cell kernel spans ~8 s - large enough to detect section
    // transitions of 8-16 bars at typical EDM tempos (120-140 BPM).
    private const int KernelHalf = 16;

    // -------------------------------------------------------------------------
    // Peak-picking thresholds
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adaptive threshold multiplier: a novelty peak must exceed
    /// mean(novelty) + <c>ThresholdSigmas</c> x std(novelty).
    /// Default 1.0 works well for EDM (clear section changes produce 2-4sigma peaks).
    /// </summary>
    public double ThresholdSigmas { get; init; } = 1.0;

    /// <summary>
    /// Minimum gap between successive boundaries in seconds.
    /// EDM phrases are typically 16-32 bars; at 128 BPM a 16-bar phrase ~ 30 s.
    /// Default 8 s allows detection of shorter 8-bar build-up transitions.
    /// </summary>
    public double MinBoundaryGapSeconds { get; init; } = 8.0;

    // -------------------------------------------------------------------------
    // Sub-band bin ranges (computed at runtime from sample rate)
    // These are index ranges into the positive half of the FFT spectrum.
    // -------------------------------------------------------------------------

    // Sub-bands for EDM (van Balen et al.; structure-detection.md Sec.EDM structure):
    //   Sub-bass :     0 -  80 Hz   (kick/rumble dominates; breakdown = near-zero)
    //   Bass     :    80 - 250 Hz   (bass line energy)
    //   Mid      :   250 - 2000 Hz  (synths, chords, vocal midrange)
    //   Hi       :  2000 - 6000 Hz  (hi-hat, synth brightness)
    // Frequencies above 6 kHz are excluded - they add noise without structural cues
    // for most EDM, and the analysis runs at 11 025 Hz (Nyquist = 5512.5 Hz).
    private const double SubBassHzMax =   80.0;
    private const double BassHzMax    =  250.0;
    private const double MidHzMax     = 2000.0;
    // Hi-band goes to Nyquist (no explicit cap needed - the loop stops at halfBins).

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Detects structural boundaries in <paramref name="audio"/> and returns them
    /// sorted by time.
    /// </summary>
    /// <param name="audio">
    /// Decoded mono audio.  The sample rate should be 11 025 Hz (matching the rest
    /// of the analysis pipeline) but any rate works - bin frequencies are computed
    /// from <see cref="JustPlay.Core.Models.DecodedAudio.SampleRate"/>.
    /// </param>
    /// <param name="bpm">
    /// Optional detected BPM.  When provided, boundary candidates are snapped to
    /// the nearest 4-bar phrase boundary within +/-2 bars.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// List of detected boundaries, sorted ascending by time.  Empty when the
    /// audio is too short (fewer than 2 x <c>PoolFrames</c> fine frames ->
    /// shorter than ~1 s) or entirely silent.
    /// </returns>
    public IReadOnlyList<StructureBoundary> Detect(
        JustPlay.Core.Models.DecodedAudio audio,
        double? bpm = null,
        CancellationToken ct = default)
    {
        var samples    = audio.Samples;
        var sampleRate = audio.SampleRate;

        if (samples is null || samples.Length < FrameSize)
            return Array.Empty<StructureBoundary>();

        // Step 1 - extract fine-grained frame feature vectors.
        var fineFrames = ExtractFineFrames(samples, sampleRate, ct);
        if (fineFrames.Count < 2)
            return Array.Empty<StructureBoundary>();

        // Step 2 - pool fine frames into coarser SSM rows.
        var ssmFrames = PoolFineFrames(fineFrames, ct);
        if (ssmFrames.Count < 2)
            return Array.Empty<StructureBoundary>();

        // Step 3 - build Self-Similarity Matrix.
        var ssm = BuildSsm(ssmFrames, ct);

        // Step 4 - Gaussian-checkerboard novelty curve.
        var novelty = ComputeNovelty(ssm, ct);

        // Step 5 - peak-pick.
        var minGapCells = (int)Math.Max(1.0,
            MinBoundaryGapSeconds / (HopSize * (double)PoolFrames / sampleRate));

        var peaks = PickPeaks(novelty, minGapCells, ThresholdSigmas);

        if (peaks.Count == 0)
            return Array.Empty<StructureBoundary>();

        // Step 6 - convert cell indices to seconds, optionally snap to beatgrid.
        double SecondsPerCell() => HopSize * (double)PoolFrames / sampleRate;
        var secPerCell = SecondsPerCell();

        // Normalise novelty for the output score.
        var maxNovelty = 0.0;
        foreach (var (_, v) in peaks) if (v > maxNovelty) maxNovelty = v;
        if (maxNovelty <= 0.0) maxNovelty = 1.0;

        var boundaries = new List<StructureBoundary>(peaks.Count);
        foreach (var (cell, value) in peaks)
        {
            var timeSec = cell * secPerCell;
            if (bpm.HasValue && bpm.Value > 0.0)
                timeSec = SnapToBeatgrid(timeSec, bpm.Value);
            boundaries.Add(new StructureBoundary(timeSec, value / maxNovelty));
        }

        // Sort (snap may reorder near-identical timestamps).
        boundaries.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return boundaries;
    }

    // -------------------------------------------------------------------------
    // Step 1 - fine-frame feature extraction
    // -------------------------------------------------------------------------

    // A compact representation of one analysis frame's spectral features.
    // Stored as a flat float[] to keep memory layout cache-friendly for the SSM dot-product.
    //   [0] Sub-bass energy
    //   [1] Bass energy
    //   [2] Mid energy
    //   [3] Hi energy
    //   [4] Spectral centroid (normalised by Nyquist)
    //   [5] Spectral flatness  (geometric/arithmetic mean ratio, already  in  [0,1])
    //   [6] Rectified spectral flux (onset density)
    // Stored L2-normalised so SSM dot-product = cosine similarity.
    private const int FeatureDim = 7;

    /// <summary>
    /// Extracts Hann-windowed FFT frames with hop <c>HopSize</c> and returns one
    /// 7-D L2-normalised feature vector per fine frame.  Adjacent-frame flux is
    /// computed in-loop to avoid a second pass.
    /// </summary>
    private static List<float[]> ExtractFineFrames(
        float[] samples, int sampleRate, CancellationToken ct)
    {
        var hann     = BuildHannWindow(FrameSize);
        var re       = new float[FrameSize];
        var im       = new float[FrameSize];
        var halfBins = FrameSize / 2;

        // Sub-band boundary bins (inclusive lower, exclusive upper).
        int BinOf(double hz) => (int)Math.Round(hz * FrameSize / sampleRate);
        var subBassEnd = Math.Min(BinOf(SubBassHzMax), halfBins);
        var bassEnd    = Math.Min(BinOf(BassHzMax),    halfBins);
        var midEnd     = Math.Min(BinOf(MidHzMax),     halfBins);
        var hiEnd      = halfBins;  // to Nyquist

        var frames  = new List<float[]>((samples.Length / HopSize) + 1);
        float[]? prevMag = null;

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
        {
            ct.ThrowIfCancellationRequested();

            // Apply Hann window.
            for (var n = 0; n < FrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }
            Fft.Forward(re, im);

            // Magnitude spectrum (positive half only).
            var mag = new float[halfBins];
            for (var k = 0; k < halfBins; k++)
                mag[k] = (float)Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);

            // --- Feature computation ---

            // Sub-band energies: mean magnitude in each band.
            var subBassE = BandMeanMag(mag, 0,          subBassEnd);
            var bassE    = BandMeanMag(mag, subBassEnd,  bassEnd);
            var midE     = BandMeanMag(mag, bassEnd,     midEnd);
            var hiE      = BandMeanMag(mag, midEnd,      hiEnd);

            // Spectral centroid: magnitude-weighted mean bin frequency, normalised by Nyquist.
            double centroid = 0.0;
            double totalMag = 0.0;
            for (var k = 0; k < halfBins; k++) { centroid += mag[k] * k; totalMag += mag[k]; }
            var centroidNorm = totalMag > 0 ? (float)(centroid / totalMag / halfBins) : 0f;

            // Spectral flatness: geometric mean / arithmetic mean (in [0,1], 1=flat/noisy).
            // Computed on log-domain to avoid underflow: exp(mean(log(|X|+epsilon))) / mean(|X|+epsilon).
            const float eps = 1e-9f;
            double logSum = 0.0, linSum = 0.0;
            for (var k = 0; k < halfBins; k++)
            {
                var m = (double)mag[k] + eps;
                logSum += Math.Log(m);
                linSum += m;
            }
            var geomMean  = Math.Exp(logSum / halfBins);
            var arithMean = linSum / halfBins;
            var flatness  = (float)(arithMean > eps ? geomMean / arithMean : 0.0);

            // Rectified spectral flux: sum max(0, |X_n| - |X_{n-1}|) / totalMag.
            var flux = 0f;
            if (prevMag is not null)
            {
                double fluxSum = 0.0;
                for (var k = 0; k < halfBins; k++)
                {
                    var diff = mag[k] - prevMag[k];
                    if (diff > 0) fluxSum += diff;
                }
                flux = totalMag > 0 ? (float)(fluxSum / totalMag) : 0f;
            }

            var vec = new float[FeatureDim]
            {
                subBassE,
                bassE,
                midE,
                hiE,
                centroidNorm,
                flatness,
                flux
            };

            L2Normalise(vec);
            frames.Add(vec);
            prevMag = mag;
        }

        return frames;
    }

    // -------------------------------------------------------------------------
    // Step 2 - pool fine frames into coarser SSM rows
    // -------------------------------------------------------------------------

    /// <summary>
    /// Mean-pools every <c>PoolFrames</c> fine frames into one SSM row vector,
    /// then L2-normalises the result.  The last partial group is included if it
    /// has at least one frame.
    /// </summary>
    private static List<float[]> PoolFineFrames(List<float[]> fine, CancellationToken ct)
    {
        var pooled = new List<float[]>((fine.Count / PoolFrames) + 1);

        for (var start = 0; start < fine.Count; start += PoolFrames)
        {
            ct.ThrowIfCancellationRequested();
            var end = Math.Min(start + PoolFrames, fine.Count);
            var acc = new float[FeatureDim];
            for (var i = start; i < end; i++)
                for (var d = 0; d < FeatureDim; d++)
                    acc[d] += fine[i][d];
            var count = end - start;
            for (var d = 0; d < FeatureDim; d++)
                acc[d] /= count;
            L2Normalise(acc);
            pooled.Add(acc);
        }

        return pooled;
    }

    // -------------------------------------------------------------------------
    // Step 3 - Self-Similarity Matrix
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the NxN cosine self-similarity matrix from <paramref name="frames"/>.
    /// Because vectors are L2-normalised, S[i,j] = dot(f_i, f_j)  in  [-1, 1],
    /// but in practice all features are non-negative so S  in  [0, 1].
    /// Stored as a flat float[] in row-major order (S[i,j] = flat[i*N + j]).
    /// </summary>
    private static float[] BuildSsm(List<float[]> frames, CancellationToken ct)
    {
        var n   = frames.Count;
        var ssm = new float[n * n];

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            ssm[i * n + i] = 1.0f;           // self-similarity is always 1
            for (var j = i + 1; j < n; j++)
            {
                var dot = 0.0f;
                for (var d = 0; d < FeatureDim; d++)
                    dot += frames[i][d] * frames[j][d];
                dot = Math.Clamp(dot, -1f, 1f);
                ssm[i * n + j] = dot;
                ssm[j * n + i] = dot;         // symmetry
            }
        }

        return ssm;
    }

    // -------------------------------------------------------------------------
    // Step 4 - Gaussian-checkerboard novelty curve
    // -------------------------------------------------------------------------

    /// <summary>
    /// Slides a Gaussian-tapered checkerboard kernel along the main diagonal of
    /// the SSM and computes a novelty value for each cell.
    ///
    /// <para>Kernel shape: a (2K)x(2K) matrix with quadrant signs:
    /// <code>
    ///   +1 | -1
    ///   --------
    ///   -1 | +1
    /// </code>
    /// Tapered by G(r,c) = exp(-(r^2+c^2) / (2-(K/2)^2)) where r,c  in  [-K, K).
    /// "Correlation" at diagonal position i = sum over kernel of kernel[r,c]-S[i+r, i+c].
    /// A large positive correlation means the neighbourhood of (i,i) in the SSM
    /// has the checkerboard pattern typical of a section boundary.
    /// [Foote ICME 2000; Mueller FMP Sec.4.4.1; structure-detection.md Sec.Foote SSM]
    /// </para>
    /// </summary>
    private static double[] ComputeNovelty(float[] ssm, CancellationToken ct)
    {
        var n      = (int)Math.Round(Math.Sqrt(ssm.Length));  // nxn matrix
        var K      = KernelHalf;
        var sigma2 = (K / 2.0) * (K / 2.0);                  // sigma^2 for Gaussian taper

        // Pre-build the Gaussian-checkerboard kernel (2K x 2K) stored flat.
        var kernelSize = 2 * K;
        var kernel     = new double[kernelSize * kernelSize];
        for (var kr = 0; kr < kernelSize; kr++)
        {
            var r   = kr - K;          // row offset relative to diagonal centre
            for (var kc = 0; kc < kernelSize; kc++)
            {
                var c   = kc - K;      // col offset
                var gau = Math.Exp(-(r * r + c * c) / (2.0 * sigma2));
                // Checkerboard sign: +1 in top-left (r<0,c<0) and bottom-right (r>=0,c>=0)
                //                   -1 in top-right (r<0,c>=0) and bottom-left (r>=0,c<0)
                var sign = ((r < 0) == (c < 0)) ? 1.0 : -1.0;
                kernel[kr * kernelSize + kc] = sign * gau;
            }
        }

        var novelty = new double[n];

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();

            var val = 0.0;
            for (var kr = 0; kr < kernelSize; kr++)
            {
                var row = i + (kr - K);
                if (row < 0 || row >= n) continue;

                for (var kc = 0; kc < kernelSize; kc++)
                {
                    var col = i + (kc - K);
                    if (col < 0 || col >= n) continue;

                    val += kernel[kr * kernelSize + kc] * ssm[row * n + col];
                }
            }

            // Only keep positive novelty - negative means "very homogeneous".
            novelty[i] = Math.Max(0.0, val);
        }

        return novelty;
    }

    // -------------------------------------------------------------------------
    // Step 5 - adaptive-threshold peak-picking
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns (cellIndex, noveltyValue) pairs for local maxima that exceed
    /// mean + <paramref name="sigmas"/> x std and satisfy the minimum inter-peak gap.
    /// </summary>
    private static List<(int Cell, double Value)> PickPeaks(
        double[] novelty, int minGapCells, double sigmas)
    {
        var n = novelty.Length;
        if (n == 0) return new List<(int, double)>();

        // Adaptive threshold: mean + sigmas*std.
        var mean = 0.0;
        for (var i = 0; i < n; i++) mean += novelty[i];
        mean /= n;

        var variance = 0.0;
        for (var i = 0; i < n; i++) { var d = novelty[i] - mean; variance += d * d; }
        variance /= n;
        var threshold = mean + sigmas * Math.Sqrt(variance);

        // Local-max detection with min-gap enforcement (greedy, highest-peak-first).
        // Collect all candidate local maxima above threshold.
        var candidates = new List<(int Cell, double Value)>();
        for (var i = 1; i < n - 1; i++)
        {
            if (novelty[i] > novelty[i - 1] && novelty[i] >= novelty[i + 1]
                && novelty[i] >= threshold)
            {
                candidates.Add((i, novelty[i]));
            }
        }

        // Greedy selection: process in descending novelty order, suppress within gap.
        candidates.Sort((a, b) => b.Value.CompareTo(a.Value));

        var selected = new List<(int Cell, double Value)>();
        var suppressed = new bool[n];

        foreach (var (cell, value) in candidates)
        {
            if (suppressed[cell]) continue;
            selected.Add((cell, value));

            // Suppress nearby cells.
            var lo = Math.Max(0, cell - minGapCells);
            var hi = Math.Min(n - 1, cell + minGapCells);
            for (var k = lo; k <= hi; k++)
                suppressed[k] = true;
        }

        return selected;
    }

    // -------------------------------------------------------------------------
    // Step 6 - beatgrid snap
    // -------------------------------------------------------------------------

    /// <summary>
    /// Snaps <paramref name="timeSec"/> to the nearest 4-bar phrase boundary within
    /// a +/-2-bar tolerance.  Returns the original time if no beatgrid multiple falls
    /// within the tolerance window.
    ///
    /// <para>A 4-bar phrase at <paramref name="bpm"/> has period
    /// 4 x 4 x (60 / bpm) = 960 / bpm seconds.  We find the nearest integer
    /// multiple of that period and snap if |error| <= 2 bars = 480 / bpm s.</para>
    /// [structure-detection.md Sec.Foote SSM + beatgrid snap; bpm-detection.md Sec.downbeat]
    /// </summary>
    private static double SnapToBeatgrid(double timeSec, double bpm)
    {
        if (bpm <= 0.0) return timeSec;

        var barDuration     = 4.0 * 60.0 / bpm;     // one 4/4 bar in seconds
        var phraseDuration  = 4.0 * barDuration;     // 4-bar phrase
        var snapTolerance   = 2.0 * barDuration;     // +/-2 bars

        var nearestPhrase  = Math.Round(timeSec / phraseDuration) * phraseDuration;
        var error          = Math.Abs(timeSec - nearestPhrase);

        return error <= snapTolerance ? nearestPhrase : timeSec;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Mean magnitude of bins in [<paramref name="lo"/>, <paramref name="hi"/>).</summary>
    private static float BandMeanMag(float[] mag, int lo, int hi)
    {
        if (hi <= lo) return 0f;
        var sum = 0.0f;
        for (var k = lo; k < hi; k++) sum += mag[k];
        return sum / (hi - lo);
    }

    /// <summary>
    /// In-place L2 normalisation.  Leaves the vector unchanged if its norm is
    /// below <c>1e-9f</c> (silence) to avoid NaN propagation.
    /// </summary>
    private static void L2Normalise(float[] v)
    {
        var norm2 = 0.0f;
        foreach (var x in v) norm2 += x * x;
        var norm = (float)Math.Sqrt(norm2);
        if (norm < 1e-9f) return;
        for (var d = 0; d < v.Length; d++) v[d] /= norm;
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }
}
