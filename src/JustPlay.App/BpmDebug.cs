using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JustPlay.Analysis;
using JustPlay.Audio.Bass;
using JustPlay.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App;

/// <summary>
/// Headless BPM diagnostic, invoked via <c>--bpm-debug &lt;file&gt;</c>.
///
/// Prints:
///   1. Raw BASS_FX BPM.
///   2. Corrected BPM from TempoOctaveCorrector (same 11 kHz decode path as TrackAnalysisService).
///   3. Octave-corrector candidate scores table {raw/2, raw, raw*2, raw/3, raw*3}.
///   4. ACF peak table - top 8 peaks across 60-200 BPM - so we can see the true
///      tempo periodicity even if it's not a 2x multiple of the BASS_FX estimate.
/// </summary>
internal static class BpmDebug
{
    // Must match TrackAnalysisService.EnergySampleRate (the corrector reuses this decode).
    private const int SampleRate = 11025;

    // The corrector's own onset-envelope parameters (replicated from TempoOctaveCorrector
    // so we use the identical frames to interpret the same ACF).
    private const int FrameSize = 512;
    private const int HopSize   = FrameSize / 4;  // 128

    // Log-Gaussian prior parameters (Ellis 2007 / librosa).
    private const double PriorCentreSec    = 0.5;  // 120 BPM
    private const double PriorSigmaOctaves = 0.9;

    // Conservative-correction margin (same as TempoOctaveCorrector.MinScoreMargin).
    private const double MinScoreMargin = 0.15;

    public static void Run(IServiceProvider services, string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            // Give a clear message if the NAS is unreachable.
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && !Directory.Exists(dir))
                Console.WriteLine($"  (parent directory also not found - NAS may be unreachable)");
            return;
        }

        Console.WriteLine($"=== BPM Diagnostic ===");
        Console.WriteLine($"File: {filePath}");
        Console.WriteLine();

        // ---- BASS init (headless, device=0 means no-sound) ----
        if (!ManagedBass.Bass.Init(0))
        {
            var err = ManagedBass.Bass.LastError;
            if (err != ManagedBass.Errors.Already)
            {
                Console.WriteLine($"BASS init failed: {err}");
                return;
            }
        }

        try
        {
            RunInternal(services, filePath);
        }
        finally
        {
            ManagedBass.Bass.Free();
        }
    }

    private static void RunInternal(IServiceProvider services, string filePath)
    {
        var bpmDetector = services.GetRequiredService<IBpmDetector>();
        var decoder     = services.GetRequiredService<IAudioDecoder>();

        // ---- 1. Raw BASS_FX BPM ----
        Console.WriteLine("--- 1. Raw BASS_FX BPM ---");
        double? rawBpm = null;
        try
        {
            rawBpm = bpmDetector.Detect(filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  BASS_FX detection failed: {ex.Message}");
        }
        Console.WriteLine(rawBpm.HasValue
            ? $"  Raw BPM: {rawBpm.Value:0.00}"
            : "  Raw BPM: (none - detection failed)");
        Console.WriteLine();

        // ---- Decode at 11 kHz (same path as TrackAnalysisService for energy + octave correction) ----
        Console.WriteLine("--- Decoding audio at 11025 Hz (same path as TrackAnalysisService) ---");
        float[]? samples = null;
        try
        {
            var audio = decoder.DecodeMono(filePath, SampleRate);
            samples = audio.Samples;
            Console.WriteLine($"  Decoded {samples?.Length ?? 0:N0} samples " +
                              $"({(samples?.Length ?? 0) / (double)SampleRate:0.1f} s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Decode failed: {ex.Message}");
        }
        Console.WriteLine();

        if (samples is null || samples.Length < FrameSize * 4)
        {
            Console.WriteLine("Audio too short or failed to decode - cannot run corrector diagnostics.");
            return;
        }

        // ---- 2. Corrected BPM ----
        Console.WriteLine("--- 2. TempoOctaveCorrector result ---");
        double? correctedBpm = null;
        if (rawBpm.HasValue)
        {
            var corrector = new TempoOctaveCorrector();
            correctedBpm = corrector.Correct(rawBpm.Value, samples, SampleRate);
            Console.WriteLine($"  Raw BPM:       {rawBpm.Value:0.00}");
            Console.WriteLine($"  Corrected BPM: {correctedBpm.Value:0.00}");
            if (Math.Abs(correctedBpm.Value - rawBpm.Value) < 0.01)
                Console.WriteLine("  (no correction applied - raw BPM was kept)");
            else
                Console.WriteLine($"  (correction applied: {rawBpm.Value:0.00} -> {correctedBpm.Value:0.00})");
        }
        else
        {
            Console.WriteLine("  (skipped - no raw BPM)");
        }
        Console.WriteLine();

        // ---- 3 + 4. Build onset envelope and ACF (replicated from TempoOctaveCorrector internals) ----
        Console.WriteLine("--- Building onset-strength envelope + ACF ---");
        var envelope = BuildOnsetEnvelope(samples);
        if (envelope is null || envelope.Length < 4)
        {
            Console.WriteLine("  Envelope too short - cannot compute ACF.");
            return;
        }
        Console.WriteLine($"  Envelope length: {envelope.Length} frames " +
                          $"(hop={HopSize} samples, ~{1000.0 * HopSize / SampleRate:0.0} ms/frame)");

        // Max lag: 40 BPM -> period 1.5 s.
        var maxLagFrames = (int)Math.Min(
            1.5 * SampleRate / HopSize,
            envelope.Length - 1);
        Console.WriteLine($"  Max lag: {maxLagFrames} frames ({1.5:0.0} s)");

        var acf = Autocorrelate(envelope, maxLagFrames);
        Console.WriteLine($"  ACF computed. ACF[0] = {acf[0]:0.000000} (total power)");
        Console.WriteLine();

        // ---- 3. Candidate scores ----
        Console.WriteLine("--- 3. Octave-corrector candidate scores ---");
        if (rawBpm.HasValue)
        {
            // Extended set for diagnosis (corrector only uses /2,x1,x2 - we also show /3,x3).
            var candidateDefs = new (string Label, double BpmFactor)[]
            {
                ("raw/3", 1.0/3.0),
                ("raw/2", 0.5),
                ("raw",   1.0),
                ("raw*2", 2.0),
                ("raw*3", 3.0),
            };

            Console.WriteLine("  " + "Candidate".PadRight(10) + "  " + "BPM".PadLeft(7) + "  " + "Lag(fr)".PadLeft(7) + "  " + "ACF".PadLeft(10) + "  " + "Prior(W)".PadLeft(10) + "  " + "Score".PadLeft(10) + "  InRange?");
            Console.WriteLine($"  {new string('-', 78)}");

            double rawScore    = double.MinValue;
            bool   rawInRange  = false;
            double bestScore   = double.MinValue;
            double bestBpm     = rawBpm.Value;

            foreach (var (label, factor) in candidateDefs)
            {
                var cBpm = rawBpm.Value * factor;
                var inRange = cBpm is >= 40.0 and <= 240.0;
                var isInCorrector = label is "raw/2" or "raw" or "raw*2";  // the corrector's actual candidates

                if (!inRange)
                {
                    Console.WriteLine($"  {label,-10}  {cBpm,7:0.00}  {"--",7}  {"--",10}  {"--",10}  {"--",10}  out of 40-240 range");
                    continue;
                }

                var periodSec = 60.0 / cBpm;
                var lagFrames = (int)Math.Round(periodSec * SampleRate / HopSize);

                double acfVal = 0;
                double weight = 0;
                double score  = 0;
                var    lagOk  = lagFrames >= 1 && lagFrames <= maxLagFrames;

                if (lagOk)
                {
                    acfVal = acf[lagFrames];
                    weight = LogGaussianWeight(periodSec);
                    score  = acfVal * weight;
                }

                var inCorrector = isInCorrector ? " (corrector cand.)" : "";
                Console.WriteLine($"  {label,-10}  {cBpm,7:0.00}  {lagFrames,7}  {acfVal,10:0.000000}  {weight,10:0.000000}  {score,10:0.000000}  {(lagOk ? "YES" : "lag out of range")}{inCorrector}");

                // Track corrector-specific best/raw for the guard explanation.
                if (isInCorrector && lagOk)
                {
                    if (score > bestScore) { bestScore = score; bestBpm = cBpm; }
                    if (Math.Abs(cBpm - rawBpm.Value) < 0.5) { rawScore = score; rawInRange = true; }
                }
            }

            Console.WriteLine();

            // Explain corrector decision.
            Console.WriteLine("--- Corrector decision trace ---");
            Console.WriteLine($"  rawInRange={rawInRange}, rawScore={rawScore:0.000000}");
            Console.WriteLine($"  bestBpm={bestBpm:0.00}, bestScore={bestScore:0.000000}");
            bool rawWon = Math.Abs(bestBpm - rawBpm.Value) < 0.5;
            if (rawWon)
                Console.WriteLine("  Decision: raw already won -> no correction.");
            else if (bestScore <= 0)
                Console.WriteLine("  Decision: bestScore <= 0 -> keep raw.");
            else if (rawInRange && rawScore > 0 && bestScore <= rawScore * (1.0 + MinScoreMargin))
                Console.WriteLine($"  Decision: margin {bestScore / rawScore - 1:+0.000;-0.000} < {MinScoreMargin} -> keep raw.");
            else
                Console.WriteLine($"  Decision: correction applied -> {bestBpm:0.00} BPM.");
        }
        else
        {
            Console.WriteLine("  (skipped - no raw BPM)");
        }
        Console.WriteLine();

        // ---- 4. ACF peak table (60-200 BPM) ----
        Console.WriteLine("--- 4. ACF peak table (60-200 BPM, top 8 peaks) ---");
        PrintAcfPeakTable(acf, SampleRate, minBpm: 60, maxBpm: 200, topN: 8);
    }

    // ---- Onset-strength envelope (identical to TempoOctaveCorrector.BuildOnsetEnvelope) ----
    private static double[]? BuildOnsetEnvelope(float[] samples)
    {
        if (samples.Length < FrameSize) return null;

        var hann      = BuildHannWindow(FrameSize);
        var re        = new float[FrameSize];
        var im        = new float[FrameSize];
        var halfBins  = FrameSize / 2;
        var prevMag   = new double[halfBins];
        var havePrev  = false;

        var frameCount = (samples.Length - FrameSize) / HopSize + 1;
        var envelope   = new double[frameCount];
        var envIdx     = 0;

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
        {
            for (var n = 0; n < FrameSize; n++) { re[n] = samples[start + n] * hann[n]; im[n] = 0f; }
            Fft.Forward(re, im);

            double flux = 0;
            for (var k = 0; k < halfBins; k++)
            {
                var mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                if (havePrev) { var diff = mag - prevMag[k]; if (diff > 0) flux += diff; }
                prevMag[k] = mag;
            }
            if (envIdx < envelope.Length)
                envelope[envIdx++] = havePrev ? flux : 0.0;
            havePrev = true;
        }
        return envelope;
    }

    // ---- Biased autocorrelation (identical to TempoOctaveCorrector.Autocorrelate) ----
    private static double[] Autocorrelate(double[] signal, int maxLag)
    {
        var n   = signal.Length;
        var acf = new double[maxLag + 1];
        for (var lag = 0; lag <= maxLag; lag++)
        {
            var sum = 0.0;
            var count = n - lag;
            for (var i = 0; i < count; i++) sum += signal[i] * signal[i + lag];
            acf[lag] = sum / n;
        }
        return acf;
    }

    // ---- Log-Gaussian prior (identical to TempoOctaveCorrector.LogGaussianWeight) ----
    private static double LogGaussianWeight(double periodSec)
    {
        if (periodSec <= 0) return 0;
        var logRatio = Math.Log2(periodSec / PriorCentreSec);
        var z = logRatio / PriorSigmaOctaves;
        return Math.Exp(-0.5 * z * z);
    }

    // ---- ACF peak table (broad BPM scan for true-tempo discovery) ----
    /// <summary>
    /// Scans every integer BPM in [<paramref name="minBpm"/>, <paramref name="maxBpm"/>],
    /// converts each to a lag, reads the ACF value, applies the prior, then picks the top
    /// <paramref name="topN"/> by combined score and prints them.
    /// </summary>
    private static void PrintAcfPeakTable(double[] acf, int sampleRate,
        double minBpm, double maxBpm, int topN)
    {
        var hopSamples = HopSize;
        var maxLag = acf.Length - 1;

        var entries = new List<(double Bpm, int Lag, double AcfVal, double Prior, double Score)>();

        for (var bpmI = (int)minBpm; bpmI <= (int)maxBpm; bpmI++)
        {
            var bpm       = (double)bpmI;
            var periodSec = 60.0 / bpm;
            var lagFrames = (int)Math.Round(periodSec * sampleRate / hopSamples);
            if (lagFrames < 1 || lagFrames > maxLag) continue;

            var acfVal = acf[lagFrames];
            var prior  = LogGaussianWeight(periodSec);
            var score  = acfVal * prior;
            entries.Add((bpm, lagFrames, acfVal, prior, score));
        }

        // Sort by combined score descending, take top N.
        var top = entries
            .OrderByDescending(e => e.Score)
            .Take(topN)
            .ToList();

        Console.WriteLine("  " + "BPM".PadLeft(5) + "  " + "Lag(fr)".PadLeft(7) + "  " + "ACF".PadLeft(10) + "  " + "Prior".PadLeft(8) + "  " + "Score".PadLeft(10));
        Console.WriteLine($"  {new string('-', 55)}");
        foreach (var (bpm, lag, acfVal, prior, score) in top)
            Console.WriteLine($"  {bpm,5:0}  {lag,7}  {acfVal,10:0.000000}  {prior,8:0.000000}  {score,10:0.000000}");

        // Also show a "raw BPM lag" raw ACF value to help explain what BASS_FX likely locked onto.
        Console.WriteLine();
        Console.WriteLine("  Full list sorted by BPM (for reference, >=score 0.0001):");
        Console.WriteLine("  " + "BPM".PadLeft(5) + "  " + "Lag(fr)".PadLeft(7) + "  " + "ACF".PadLeft(10) + "  " + "Prior".PadLeft(8) + "  " + "Score".PadLeft(10));
        Console.WriteLine($"  {new string('-', 55)}");
        foreach (var (bpm, lag, acfVal, prior, score) in entries
                     .Where(e => e.Score >= 0.0001)
                     .OrderBy(e => e.Bpm))
            Console.WriteLine($"  {bpm,5:0}  {lag,7}  {acfVal,10:0.000000}  {prior,8:0.000000}  {score,10:0.000000}");
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (var n = 0; n < size; n++)
            w[n] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (size - 1))));
        return w;
    }

    // =========================================================================
    // --bpm-validate <folder>
    // =========================================================================

    /// <summary>
    /// Batch BPM validation harness.
    ///
    /// For each audio file in <paramref name="folder"/>:
    ///   - reads the tagged BPM (TagLib#, ground truth from e.g. Mixed In Key),
    ///   - computes raw BASS_FX BPM,
    ///   - runs OLD corrector logic (only {raw/2, raw, rawx2}),
    ///   - runs NEW corrector logic (hybrid ACF scan; full <see cref="TempoOctaveCorrector"/>),
    ///   - prints a table and summary.
    ///
    /// STRICTLY READ-ONLY - no writes to the folder at any point.
    /// </summary>
    public static void RunValidate(IServiceProvider services, string folder)
    {
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            return;
        }

        // ---- BASS init (headless, device=0) ----
        if (!ManagedBass.Bass.Init(0))
        {
            var err = ManagedBass.Bass.LastError;
            if (err != ManagedBass.Errors.Already)
            {
                Console.WriteLine($"BASS init failed: {err}");
                return;
            }
        }

        try
        {
            RunValidateInternal(services, folder);
        }
        finally
        {
            ManagedBass.Bass.Free();
        }
    }

    private static void RunValidateInternal(IServiceProvider services, string folder)
    {
        var bpmDetector  = services.GetRequiredService<IBpmDetector>();
        var decoder      = services.GetRequiredService<IAudioDecoder>();
        var metaReader   = services.GetRequiredService<IMetadataReader>();
        var newCorrector = new TempoOctaveCorrector();

        // Supported audio extensions - same as the main app.
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".wav", ".aiff", ".aif", ".m4a", ".ogg", ".opus" };

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileName(f))
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine("No audio files found.");
            return;
        }

        Console.WriteLine($"=== BPM Batch Validation ===");
        Console.WriteLine($"Folder : {folder}");
        Console.WriteLine($"Files  : {files.Length}");
        Console.WriteLine();

        // Column header
        const int nameW  = 40;
        const int numW   = 8;
        const int boolW  = 9;
        var header = $"{"File",-nameW}  {"TagBPM",numW}  {"RawBASS",numW}  {"OldCorr",numW}  {"NewCorr",numW}  {"OldOk?",boolW}  {"NewOk?",boolW}  {"OldOctOk?",10}  {"NewOctOk?",10}  Note";
        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length + 20));

        // Counters for summary
        int tracked      = 0;  // files with a tagged BPM
        int oldMatch     = 0;
        int newMatch     = 0;
        int oldOctMatch  = 0;
        int newOctMatch  = 0;
        int changed      = 0;
        int changedToward = 0;
        int changedAway  = 0;

        foreach (var filePath in files)
        {
            var name = Path.GetFileName(filePath);
            var shortName = name.Length > nameW ? name[..(nameW - 1)] + "..." : name;

            // -- Read tagged BPM (READ ONLY) --
            double? taggedBpm = null;
            try
            {
                var meta = metaReader.Read(filePath);
                if (meta.TaggedBpm is > 0)
                    taggedBpm = meta.TaggedBpm;
            }
            catch { /* tag read failure is non-fatal */ }

            // -- Raw BASS_FX BPM --
            double? rawBpm = null;
            try { rawBpm = bpmDetector.Detect(filePath); }
            catch { /* BASS failure is non-fatal */ }

            // -- Decode at 11 kHz for correctors --
            float[]? samples = null;
            int      sampleRate = SampleRate;
            try
            {
                var audio = decoder.DecodeMono(filePath, SampleRate);
                samples    = audio.Samples;
                sampleRate = audio.SampleRate;
            }
            catch { /* decode failure is non-fatal */ }

            // -- OLD corrector: only raw/2, raw, raw*2 (replicating old logic inline) --
            double? oldCorrected = rawBpm;
            if (rawBpm.HasValue && samples is not null && samples.Length >= 512)
                oldCorrected = OldCorrect(rawBpm.Value, samples, sampleRate);

            // -- NEW corrector: full hybrid ACF-scan (TempoOctaveCorrector) --
            double? newCorrected = rawBpm;
            if (rawBpm.HasValue && samples is not null && samples.Length >= 512)
                newCorrected = newCorrector.Correct(rawBpm.Value, samples, sampleRate);

            // -- Scoring --
            bool oldOk = false, newOk = false, oldOctOk = false, newOctOk = false;
            string note = "";

            if (taggedBpm.HasValue)
            {
                tracked++;
                var t = taggedBpm.Value;

                if (oldCorrected.HasValue) { oldOk = WithinBpm(oldCorrected.Value, t, 2.0); oldOctOk = OctaveMatch(oldCorrected.Value, t); }
                if (newCorrected.HasValue) { newOk = WithinBpm(newCorrected.Value, t, 2.0); newOctOk = OctaveMatch(newCorrected.Value, t); }
                if (oldOk) oldMatch++;
                if (newOk) newMatch++;
                if (oldOctOk) oldOctMatch++;
                if (newOctOk) newOctMatch++;

                // Track changes
                if (oldCorrected.HasValue && newCorrected.HasValue &&
                    Math.Abs(newCorrected.Value - oldCorrected.Value) > 1.0)
                {
                    changed++;
                    var oldDist = Math.Abs(oldCorrected.Value - t);
                    var newDist = Math.Abs(newCorrected.Value - t);
                    if (newDist < oldDist) { changedToward++; note = "IMPROVED"; }
                    else                  { changedAway++;   note = "REGRESSED"; }
                }
            }
            else
            {
                note = "no tag";
            }

            // -- Print row --
            var oldOkStr    = taggedBpm.HasValue ? (oldOk    ? "YES" : "no") : "--";
            var newOkStr    = taggedBpm.HasValue ? (newOk    ? "YES" : "no") : "--";
            var oldOctStr   = taggedBpm.HasValue ? (oldOctOk ? "YES" : "no") : "--";
            var newOctStr   = taggedBpm.HasValue ? (newOctOk ? "YES" : "no") : "--";
            Console.WriteLine(
                $"{shortName,-nameW}  " +
                $"{FmtBpm(taggedBpm),numW}  " +
                $"{FmtBpm(rawBpm),numW}  " +
                $"{FmtBpm(oldCorrected),numW}  " +
                $"{FmtBpm(newCorrected),numW}  " +
                $"{oldOkStr,boolW}  " +
                $"{newOkStr,boolW}  " +
                $"{oldOctStr,10}  " +
                $"{newOctStr,10}  " +
                note);
        }

        // -- Summary --
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Files with tagged BPM : {tracked} / {files.Length}");
        if (tracked > 0)
        {
            Console.WriteLine($"Exact match (+/-2 BPM):");
            Console.WriteLine($"  OLD corrector : {oldMatch}/{tracked}  ({oldMatch * 100.0 / tracked:0.0}%)");
            Console.WriteLine($"  NEW corrector : {newMatch}/{tracked}  ({newMatch * 100.0 / tracked:0.0}%)");
            Console.WriteLine($"Octave-agnostic match (x// 2,3,4 within +/-2):");
            Console.WriteLine($"  OLD corrector : {oldOctMatch}/{tracked}  ({oldOctMatch * 100.0 / tracked:0.0}%)");
            Console.WriteLine($"  NEW corrector : {newOctMatch}/{tracked}  ({newOctMatch * 100.0 / tracked:0.0}%)");
            Console.WriteLine($"Tracks changed OLD->NEW : {changed}  (improved: {changedToward}, regressed: {changedAway})");
        }
    }

    // ---- OLD corrector logic (only {raw/2, raw, raw*2}) replicated for comparison ----
    private static double OldCorrect(double rawBpm, float[] samples, int sampleRate)
    {
        if (rawBpm <= 0 || samples is null || samples.Length == 0) return rawBpm;

        var envelope = BuildOnsetEnvelope(samples);
        if (envelope is null || envelope.Length < 4) return rawBpm;

        const double maxLagSec = 1.5;
        var maxLagFrames = (int)Math.Min(
            maxLagSec * sampleRate / HopSize,
            envelope.Length - 1);
        if (maxLagFrames < 2) return rawBpm;

        var acf = Autocorrelate(envelope, maxLagFrames);

        double[] candidates = [rawBpm / 2.0, rawBpm, rawBpm * 2.0];
        var bestBpm   = rawBpm;
        var bestScore = double.MinValue;
        var rawScore  = double.MinValue;
        var rawInRange = false;

        foreach (var cBpm in candidates)
        {
            if (cBpm < 40.0 || cBpm > 240.0) continue;
            var periodSec = 60.0 / cBpm;
            var lagFrames = (int)Math.Round(periodSec * sampleRate / HopSize);
            if (lagFrames < 1 || lagFrames > maxLagFrames) continue;

            var acfVal = acf[lagFrames];
            var weight = LogGaussianWeight(periodSec);
            var score  = acfVal * weight;

            if (score > bestScore) { bestScore = score; bestBpm = cBpm; }
            if (Math.Abs(cBpm - rawBpm) < 0.5) { rawScore = score; rawInRange = true; }
        }

        if (Math.Abs(bestBpm - rawBpm) < 0.5) return rawBpm;
        if (bestScore <= 0) return rawBpm;
        if (rawInRange && rawScore > 0 && bestScore <= rawScore * 1.15) return rawBpm;
        return bestBpm;
    }

    // ---- Scoring helpers ----
    private static bool WithinBpm(double detected, double tagged, double tolerance) =>
        Math.Abs(detected - tagged) <= tolerance;

    private static bool OctaveMatch(double detected, double tagged)
    {
        // True if detected ~ tagged x (1, 2, 3, 4, 1/2, 1/3, 1/4) within +/-2 BPM.
        double[] ratios = [1.0, 2.0, 3.0, 4.0, 0.5, 1.0/3.0, 0.25];
        foreach (var r in ratios)
            if (Math.Abs(detected - tagged * r) <= 2.0) return true;
        return false;
    }

    private static string FmtBpm(double? bpm) =>
        bpm.HasValue ? bpm.Value.ToString("0.0") : "--";
}
