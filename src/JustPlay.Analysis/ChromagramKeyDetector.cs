using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Musical-key detection via a chromagram + a Krumhansl-Schmuckler-style profile
/// correlation (using the empirical Albrecht &amp; Shanahan profiles). This is the
/// headline DJ feature.
///
/// <para><b>v2 - tuned for real EDM, not just clean synthetic tones.</b> The original
/// power-spectrum + plain-sum chromagram clustered on a handful of keys (a strong 8A /
/// A-minor bias) on dense dance mixes, because the loudest bins (tuned kick / sub-bass)
/// dominated the squared magnitudes and breakdown/drop frames swamped the sum. This
/// rework targets robustness on noisy, bass-heavy material.</para>
///
/// Pipeline (all platform-agnostic managed DSP, no NuGet, no reflection):
/// <list type="number">
///   <item>Frame the mono signal (8192-sample frames, 50% hop), Hann-windowed. 8192 was
///         measured as the resolution sweet spot (4096->62%, 8192->64%, 16384->59% on the
///         EDM benchmark): finer low-octave bins than 4096 without 16384's over-smoothing.</item>
///   <item>Forward FFT each frame (<see cref="Fft"/>, our own radix-2 implementation).</item>
///   <item>Take <b>linear magnitude</b> (<c>|X|</c>), <b>not</b> power (<c>|X|^2</c>). Power
///         over-weights the kick/bass fundamentals and biases the tonic; magnitude keeps
///         midrange harmony competitive. Bins are also <b>band-limited to ~100-5000 Hz</b>
///         (sub-bass below ~100 Hz is usually the tuned kick, not harmony - lowering the cut
///         to ~65 Hz to admit the bassline root was tried and measurably regressed the
///         benchmark; the very top is noise) and folded into a <b>36-bin</b> chroma
///         (3 bins/semitone) so a global tuning offset can be estimated and corrected
///         before folding to 12.</item>
///   <item>Each frame is <b>peak-emphasised</b> (a soft spectral-whitening that rewards
///         sharp tonal peaks over flat broadband noise) and <b>L2-normalised</b> so a loud
///         drop frame counts no more than a quiet intro frame.</item>
///   <item>Aggregate the per-frame 36-bin chroma with a <b>per-bin median</b> across frames
///         (robust to transient breakdowns / drops), estimate the tuning offset from the
///         sub-semitone energy distribution, fold to 12 pitch classes, and normalise.</item>
///   <item>Match (cosine similarity, see <see cref="CosineSimilarityRotated"/>) against the
///         24 <b>EDMA</b> key profiles (major + minor, each rotated to all 12 tonics), with a
///         small additive minor-mode bias to break relative-key ties toward minor (real EDM is
///         overwhelmingly minor). Highest wins.</item>
/// </list>
///
/// <para><b>What moved the needle</b> on the real-EDM benchmark (~40 MIK-tagged tracks,
/// "harmonically ok" = exact + relative + fifth-neighbour): the original power + plain-sum +
/// K-S detector scored ~41% with a heavy single-key (8A) bias. Magnitude + midrange band +
/// per-frame-L2 + median aggregation killed the bias and lifted it to ~54%; switching the
/// K-S profiles for Albrecht &amp; Shanahan and adding the minor bias fixed a systematic
/// parallel-major mis-call and lifted exact accuracy from 31% to 44% (~59% harmonically ok).
/// An explicit harmonic-summation pass was tried and removed - it re-introduced a
/// dominant/fifth bias (see <see cref="FoldToTwelve"/>).</para>
///
/// <para><b>Confidence</b> is unchanged in meaning: the relative margin to the runner-up,
/// <c>(best - secondBest) / best</c>, clamped to [0, 1]. Near 0 means two keys (often the
/// relative major/minor pair) tied - a genuinely ambiguous track - while near 1 means one
/// key dominated. It reports how *decisive* the call was, which is more useful to a DJ than
/// the raw correlation (high for almost any tonal signal).</para>
/// </summary>
public sealed class ChromagramKeyDetector : IKeyDetector
{
    // 8192 (~743 ms @ 11025 Hz, 1.35 Hz/bin) is the measured sweet spot on the EDM
    // benchmark: 4096 -> 62%, 8192 -> 64% (and fewest clear "off" calls), 16384 -> 59%
    // (its ~1.5 s window over-smooths across chord changes and leaves too few frames
    // for the per-bin median). Finer low-octave resolution than 4096 without blurring.
    private const int FrameSize = 8192;
    private const int HopSize = FrameSize / 2;       // 50% overlap
    private const double C0Hz = 16.3515978312874;    // C0 reference frequency
    private const double MinFreqHz = 100.0;          // skip sub-bass / tuned-kick region (lowering to ~65 Hz let the kick back in and regressed the benchmark)
    private const double MaxFreqHz = 5000.0;         // skip very-high noise region
    private const double SilenceFloor = 1e-9;        // aggregate energy below this == silence

    private const int BinsPerSemitone = 3;           // sub-semitone resolution for tuning
    private const int ChromaBins = 12 * BinsPerSemitone; // 36-bin fine chroma

    // Additive nudge applied to every minor-key similarity to break relative major/minor
    // ties in favour of minor (see the Classify loop). With the EDMA profiles + Pearson this
    // needed ~0.06; with the EDMA profiles + cosine it saturated at ~0.02. With the current
    // braw profiles + cosine a profilexbias sweep (see KeyReport.RunBiasSweep) found bias 0.0
    // is *best* (30/39 harmonically ok, 44% exact) and any positive bias is slightly worse -
    // braw is the un-gated BeatPort median, less peaked than bgate/edma, so cosine balances
    // major/minor on its own without a thumb on the scale. Kept as a tunable (=0) rather than
    // ripped out, so the lever is one constant away if a larger benchmark says otherwise.
    private const double MinorBias = 0.0;

    // Key profiles, tonic = pitch class 0. These are the **braw** profiles of Faraldo et al.
    // (2017) - the median pitch-profile of a BeatPort EDM subset. Chosen by a profilexbias
    // sweep over the EDM benchmark: braw beat the previously-shipped EDMA on *exact* accuracy
    // (44% vs 38%) at equal harmonically-ok (77% vs 74%, a one-track edge), and topped exact
    // across all five Essentia EDM/dance profiles tried (edma/bgate/braw/shaath/edmm).
    // Verified verbatim against the Essentia source (MTG/essentia,
    // src/algorithms/tonal/key.cpp, profileTypes "braw").
    //
    // Prior shipped EDMA (kept for the record): derived by *automatic* corpus extraction,
    //   Major: 1.00, 0.29, 0.50, 0.40, 0.60, 0.56, 0.32, 0.80, 0.31, 0.45, 0.42, 0.39
    //   Minor: 1.00, 0.31, 0.44, 0.58, 0.33, 0.49, 0.29, 0.78, 0.43, 0.29, 0.53, 0.32
    private static readonly double[] MajorProfile =
        [1.0000, 0.1573, 0.4200, 0.1570, 0.5296, 0.3669, 0.1632, 0.7711, 0.1676, 0.3827, 0.2113, 0.2965];

    private static readonly double[] MinorProfile =
        [1.0000, 0.2330, 0.3615, 0.3905, 0.2925, 0.3777, 0.1961, 0.7425, 0.2701, 0.2161, 0.4228, 0.2272];

    public (MusicalKey Key, double Confidence)? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        var chroma = BuildChroma(audio, ct);
        return chroma is null ? null : Classify(chroma, MinorBias);
    }

    /// <summary>
    /// Builds the final normalised 12-bin chroma vector for a track (the expensive,
    /// FFT-heavy stage) - fine chromagram -> tuning-corrected fold to 12 -> unit-sum
    /// normalise. Returns null for silence / no tonal content. Exposed (with
    /// <see cref="Classify"/>) so tuning harnesses can decode + build the chroma ONCE and
    /// then cheaply re-score it under different classifier parameters, instead of paying
    /// the decode + FFT cost per parameter value.
    /// </summary>
    public double[]? BuildChroma(DecodedAudio audio, CancellationToken ct = default)
    {
        var samples = audio.Samples;
        var sampleRate = audio.SampleRate;

        // Need at least one full frame and a sane sample rate to say anything.
        if (samples is null || samples.Length < FrameSize || sampleRate <= 0)
            return null;

        var fine = BuildFineChromagram(samples, sampleRate, ct);
        if (fine is null)
            return null; // silence / no tonal content

        var chroma = FoldToTwelve(fine);
        Normalize(chroma);
        return chroma;
    }

    /// <summary>
    /// Classifies a normalised 12-bin chroma into a key + confidence: cosine similarity
    /// against the 24 rotated EDMA profiles, with an additive <paramref name="minorBias"/>
    /// breaking relative major/minor ties toward minor. Cheap (no FFT) - the per-parameter
    /// half of <see cref="Detect"/>, split out so a sweep can re-score one chroma many times.
    /// </summary>
    public static (MusicalKey Key, double Confidence)? Classify(double[] chroma, double minorBias)
        => Classify(chroma, minorBias, MajorProfile, MinorProfile);

    /// <summary>
    /// As <see cref="Classify(double[], double)"/> but with caller-supplied major/minor
    /// profile vectors - used by the profile-sweep harness to A/B alternative profiles
    /// (bgate/braw/edmm/shaath) against the shipped EDMA without rebuilding the chroma.
    /// </summary>
    public static (MusicalKey Key, double Confidence)? Classify(
        double[] chroma, double minorBias, double[] majorProfile, double[] minorProfile)
    {
        // Correlate against all 24 rotated profiles; track the best two.
        var best = double.NegativeInfinity;
        var second = double.NegativeInfinity;
        var bestPitch = 0;
        var bestMode = KeyMode.Major;

        for (var tonic = 0; tonic < 12; tonic++)
        {
            var corrMajor = CosineSimilarityRotated(chroma, majorProfile, tonic);
            Consider(corrMajor, tonic, KeyMode.Major, ref best, ref second, ref bestPitch, ref bestMode);

            // Small additive minor-mode bias. A major key and its relative minor (e.g. C
            // major / A minor) share all seven notes, so their chroma similarities come out
            // within a hair of each other and the call flips on noise - empirically (see the
            // EDM benchmark) the flip landed on the *major* far too often, since real EDM is
            // overwhelmingly minor. Nudging the minor score by a fixed amount tips genuine
            // near-ties toward minor while leaving clearly-major tracks (margin well above
            // the bias) untouched. NB: this is on the cosine ~[0,1] scale - smaller than the
            // value that suited the old Pearson [-1,1] classifier.
            var corrMinor = CosineSimilarityRotated(chroma, minorProfile, tonic) + minorBias;
            Consider(corrMinor, tonic, KeyMode.Minor, ref best, ref second, ref bestPitch, ref bestMode);
        }

        if (double.IsNegativeInfinity(best) || double.IsNaN(best))
            return null;

        // Margin-to-runner-up confidence. Guard the divide; clamp to [0, 1].
        double confidence;
        if (best <= 0 || double.IsNegativeInfinity(second))
            confidence = 0;
        else
            confidence = Math.Clamp((best - second) / best, 0.0, 1.0);

        return (new MusicalKey(bestPitch, bestMode), confidence);
    }

    private static void Consider(
        double corr, int tonic, KeyMode mode,
        ref double best, ref double second,
        ref int bestPitch, ref KeyMode bestMode)
    {
        if (corr > best)
        {
            second = best;
            best = corr;
            bestPitch = tonic;
            bestMode = mode;
        }
        else if (corr > second)
        {
            second = corr;
        }
    }

    /// <summary>
    /// Builds a robust 36-bin (3 bins/semitone) chromagram by taking the per-bin median of
    /// the peak-emphasised, L2-normalised magnitude chroma of every frame. Returns null if
    /// the whole signal is effectively silent (no usable spectral energy).
    ///
    /// <para>Why median, not sum: a long quiet intro followed by a loud drop would let the
    /// drop's few loud frames dominate a sum. Per-frame L2 normalisation already equalises
    /// loudness across frames, and the median then ignores the minority of frames whose
    /// content disagrees (a noisy breakdown, a vocal-only bridge), keeping the harmonically
    /// stable core of the track.</para>
    /// </summary>
    private static double[]? BuildFineChromagram(float[] samples, int sampleRate, CancellationToken ct)
    {
        var nyquist = sampleRate / 2.0;
        var maxFreq = Math.Min(MaxFreqHz, nyquist);
        var hann = BuildHannWindow(FrameSize);

        // Reusable FFT scratch buffers (FrameSize is already a power of two).
        var re = new float[FrameSize];
        var im = new float[FrameSize];
        var mag = new double[FrameSize / 2];

        // Pre-map each usable FFT bin to a fine (36-bucket) chroma index so the inner loop
        // is cheap. binToFine[k] == -1 means "ignore this bin" (out of the harmonic band).
        var halfBins = FrameSize / 2;
        var binToFine = new int[halfBins];
        for (var k = 0; k < halfBins; k++)
        {
            var freq = k * (double)sampleRate / FrameSize;
            if (freq < MinFreqHz || freq > maxFreq || freq <= 0)
            {
                binToFine[k] = -1;
                continue;
            }
            // 36 bins/octave; bin 0 centred on C.
            var fine = (int)Math.Round(ChromaBins * Math.Log2(freq / C0Hz));
            binToFine[k] = ((fine % ChromaBins) + ChromaBins) % ChromaBins;
        }

        // Count frames so we can store one normalised chroma column per frame for medians.
        var frameCount = 0;
        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
            frameCount++;
        if (frameCount == 0)
            return null;

        // Column-major store: perBin[b] holds frameCount values for bin b (for medians).
        var perBin = new double[ChromaBins][];
        for (var b = 0; b < ChromaBins; b++)
            perBin[b] = new double[frameCount];

        var frameChroma = new double[ChromaBins];
        var anyEnergy = false;
        var f = 0;

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize, f++)
        {
            ct.ThrowIfCancellationRequested();

            // Window the frame into the FFT real buffer; clear imaginary part.
            for (var n = 0; n < FrameSize; n++)
            {
                re[n] = samples[start + n] * hann[n];
                im[n] = 0f;
            }

            Fft.Forward(re, im);

            // Linear magnitude (not power).
            for (var k = 0; k < halfBins; k++)
                mag[k] = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);

            // Peak emphasis / soft whitening: keep only the amount by which a bin exceeds
            // its immediate spectral neighbourhood, so flat broadband noise contributes
            // little and sharp tonal peaks dominate.
            Array.Clear(frameChroma, 0, ChromaBins);
            for (var k = 1; k < halfBins - 1; k++)
            {
                var fine = binToFine[k];
                if (fine < 0)
                    continue;

                // Local floor = smaller of the two neighbours; subtract it to whiten.
                var floor = Math.Min(mag[k - 1], mag[k + 1]);
                var peak = mag[k] - floor;
                if (peak > 0)
                    frameChroma[fine] += peak;
            }

            // L2-normalise this frame so loud and quiet frames count equally.
            var energy = 0.0;
            for (var b = 0; b < ChromaBins; b++)
                energy += frameChroma[b] * frameChroma[b];

            if (energy > 0)
            {
                var inv = 1.0 / Math.Sqrt(energy);
                for (var b = 0; b < ChromaBins; b++)
                    perBin[b][f] = frameChroma[b] * inv;
                anyEnergy = true;
            }
            // else: leave this frame's row at zeros (it contributes a 0 to each median).
        }

        if (!anyEnergy)
            return null;

        // Per-bin median across frames.
        var fineChroma = new double[ChromaBins];
        var total = 0.0;
        for (var b = 0; b < ChromaBins; b++)
        {
            fineChroma[b] = Median(perBin[b]);
            total += fineChroma[b];
        }

        if (total <= SilenceFloor)
            return null;

        return fineChroma;
    }

    /// <summary>
    /// Folds a 36-bin (3 bins/semitone) fine chroma down to 12 pitch classes after
    /// estimating and correcting a global tuning offset. The offset is the sub-semitone
    /// bin (-1, 0, +1 within each semitone) carrying the most energy summed over all 12
    /// semitones - i.e. whether the track sits flat, on-pitch, or sharp of A440 - so a
    /// slightly-detuned track still lands on the right class.
    ///
    /// <para>(An earlier experiment added explicit harmonic-summation here - re-crediting
    /// each pitch class's energy to the fundamental it could be the fifth/third harmonic of.
    /// It <i>worsened</i> the benchmark, introducing a dominant-key bias: tonics were pulled
    /// onto their perfect-fifth neighbour. The K-S/A&amp;S profiles already encode the
    /// expected weight of the fifth and third, so a second harmonic pass double-counts.
    /// Left out deliberately.)</para>
    /// </summary>
    private static double[] FoldToTwelve(double[] fine)
    {
        // Energy in each of the 3 sub-semitone offsets, summed across all semitones.
        var subEnergy = new double[BinsPerSemitone];
        for (var b = 0; b < ChromaBins; b++)
            subEnergy[b % BinsPerSemitone] += fine[b];

        // Pick the dominant sub-bin as the tuning centre.
        var bestSub = 0;
        for (var s = 1; s < BinsPerSemitone; s++)
            if (subEnergy[s] > subEnergy[bestSub])
                bestSub = s;

        // Fold: each 12-pitch-class bin gathers its 3 sub-bins, centred on the dominant
        // tuning offset. Full weight at the tuning centre, half at the immediate neighbours.
        var chroma = new double[12];
        for (var pc = 0; pc < 12; pc++)
        {
            var center = pc * BinsPerSemitone + bestSub;
            chroma[pc] += fine[Mod(center, ChromaBins)];
            chroma[pc] += 0.5 * fine[Mod(center - 1, ChromaBins)];
            chroma[pc] += 0.5 * fine[Mod(center + 1, ChromaBins)];
        }
        return chroma;
    }

    private static int Mod(int x, int m) => ((x % m) + m) % m;

    private static double Median(double[] values)
    {
        if (values.Length == 0)
            return 0.0;
        // Copy so we don't disturb the caller's buffer ordering (defensive; cheap at 36 bins).
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        var mid = copy.Length / 2;
        return (copy.Length & 1) == 1
            ? copy[mid]
            : 0.5 * (copy[mid - 1] + copy[mid]);
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }

    /// <summary>Scales the chroma vector to unit sum (stable for correlation).</summary>
    private static void Normalize(double[] chroma)
    {
        var sum = 0.0;
        for (var i = 0; i < chroma.Length; i++)
            sum += chroma[i];
        if (sum <= 0)
            return;
        for (var i = 0; i < chroma.Length; i++)
            chroma[i] /= sum;
    }

    /// <summary>
    /// Cosine similarity between the observed chroma and a key profile rotated so its
    /// tonic sits at pitch class <paramref name="tonic"/>.
    ///
    /// <para><b>Why cosine, not Pearson (experiment, deep-research 2026-06-01).</b> Sha'ath's
    /// libKeyFinder thesis (Sec.4.3) found the classification metric mattered more than any
    /// other parameter and that cosine similarity was "consistently more effective than
    /// correlation" on dance material. Pearson mean-centres both vectors before the dot
    /// product; cosine does not. For non-negative chroma/profile vectors that centring can
    /// flip the sign of a slot and reward a profile for matching the *absence* of energy as
    /// much as its presence. Cosine compares pure direction, which better reflects "the
    /// profile this chroma points most like". Range is ~[0,1] here (both operands
    /// non-negative), so the additive <see cref="MinorBias"/> nudge still applies on a
    /// comparable scale. Single-lever A/B against the prior Pearson detector (which scored
    /// 64% "harmonically ok" on the EDM benchmark) - Pearson is preserved in git history.</para>
    ///
    /// <para><b>Measured result (n=39 EDM benchmark): 64% -> 74% "harmonically ok"</b> - a
    /// clear win on the DJ-relevant (mixable) metric. Nuance: exact dipped (~44% -> 38%) and
    /// relative-pair calls dropped to 0%, with fifth-neighbour calls rising to 36%. The
    /// <see cref="MinorBias"/> was tuned on the Pearson [-1,1] scale; on the cosine ~[0,1]
    /// scale it over-nudges, pushing former relative-pair calls onto a fifth neighbour (still
    /// mixable). Re-tuning <see cref="MinorBias"/> for the cosine scale is the next one-lever
    /// experiment - likely recovers exact/relative without losing the harmonic-ok gain.</para>
    /// </summary>
    private static double CosineSimilarityRotated(double[] chroma, double[] profile, int tonic)
    {
        var dot = 0.0;
        var normChroma = 0.0;
        var normProfile = 0.0;
        for (var i = 0; i < 12; i++)
        {
            // Rotate the profile: profile index that lands on chroma slot i.
            var p = profile[((i - tonic) % 12 + 12) % 12];
            var c = chroma[i];
            dot += c * p;
            normChroma += c * c;
            normProfile += p * p;
        }

        var denom = Math.Sqrt(normChroma * normProfile);
        return denom <= 0 ? 0.0 : dot / denom;
    }
}
