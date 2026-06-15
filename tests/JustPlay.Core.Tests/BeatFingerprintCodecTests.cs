using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Verifies that the <see cref="BeatFingerprint"/> round-trips correctly through
/// <see cref="AnalysisStateCodec"/> — i.e. that a fingerprint stored in the JUSTPLAY
/// blob is restored exactly after deserialisation.
///
/// These tests live in JustPlay.Core.Tests because all types involved
/// (BeatFingerprint, AnalysisResult, TrackAnalysisState, AnalysisStateCodec) are in
/// JustPlay.Core — no dependency on JustPlay.Analysis is needed.
/// </summary>
public class BeatFingerprintCodecTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a synthetic fingerprint with known values so we can assert exact equality
    /// after a round-trip without floating-point drift.
    /// </summary>
    private static BeatFingerprint MakeFingerprint(
        int stLen  = 64,
        int ctLen  = 24,
        float seed = 1.0f)
    {
        // L2-normalised ST vector: all equal values = 1/sqrt(stLen) * seed.
        // Exact enough for round-trip; value is repeatable.
        var stMag = seed / MathF.Sqrt(stLen);
        var st = new float[stLen];
        for (var i = 0; i < stLen; i++) st[i] = stMag;

        var ctMag = seed / MathF.Sqrt(ctLen);
        var ct = new float[ctLen];
        for (var i = 0; i < ctLen; i++) ct[i] = ctMag;

        return new BeatFingerprint(st, ct, danceability: seed * 1.5f);
    }

    private static TrackAnalysisState MakeState(BeatFingerprint? fingerprint) =>
        new()
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm         = 128.0,
                Key         = new MusicalKey(0, KeyMode.Major),
                KeyConfidence = 0.90,
                Energy      = 7,
                Fingerprint = fingerprint,
            },
        };

    // -------------------------------------------------------------------------
    // 1. Fingerprint round-trips through Serialize / TryParse
    // -------------------------------------------------------------------------

    [Fact]
    public void Fingerprint_RoundTrips_ScaleTransform()
    {
        var fp      = MakeFingerprint();
        var state   = MakeState(fp);
        var blob    = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Detected.Fingerprint);

        var fpRestored = restored.Detected.Fingerprint!;

        Assert.Equal(fp.ScaleTransform.Length, fpRestored.ScaleTransform.Length);
        for (var i = 0; i < fp.ScaleTransform.Length; i++)
            Assert.Equal(fp.ScaleTransform[i], fpRestored.ScaleTransform[i], precision: 5);
    }

    [Fact]
    public void Fingerprint_RoundTrips_CyclicTempogram()
    {
        var fp       = MakeFingerprint();
        var state    = MakeState(fp);
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        var fpRestored = restored!.Detected.Fingerprint!;

        Assert.Equal(fp.CyclicTempogram.Length, fpRestored.CyclicTempogram.Length);
        for (var i = 0; i < fp.CyclicTempogram.Length; i++)
            Assert.Equal(fp.CyclicTempogram[i], fpRestored.CyclicTempogram[i], precision: 5);
    }

    [Fact]
    public void Fingerprint_RoundTrips_Danceability()
    {
        var fp       = MakeFingerprint(seed: 2.3f);
        var state    = MakeState(fp);
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        var fpRestored = restored!.Detected.Fingerprint!;

        // Single float serialised as JSON number — precision to 5 decimal places is fine.
        Assert.Equal(fp.Danceability, fpRestored.Danceability, precision: 5);
    }

    // -------------------------------------------------------------------------
    // 2. Null fingerprint round-trips as null (backwards-compatible)
    // -------------------------------------------------------------------------

    [Fact]
    public void NullFingerprint_RoundTrips_AsNull()
    {
        var state    = MakeState(fingerprint: null);
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.Fingerprint);
    }

    // -------------------------------------------------------------------------
    // 3. Other fields are unaffected when fingerprint is present
    // -------------------------------------------------------------------------

    [Fact]
    public void OtherFields_ArePreserved_WhenFingerprintIsPresent()
    {
        var fp       = MakeFingerprint();
        var state    = MakeState(fp);
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.Equal(128.0, restored!.Detected.Bpm);
        Assert.Equal(new MusicalKey(0, KeyMode.Major), restored.Detected.Key);
        Assert.Equal(0.90, restored.Detected.KeyConfidence);
        Assert.Equal(7, restored.Detected.Energy);
        Assert.NotNull(restored.Detected.Fingerprint);  // fingerprint also present
    }

    // -------------------------------------------------------------------------
    // 4. Blob without fingerprint fields (simulating an older v3 blob) is parsed
    //    gracefully — Fingerprint stays null, no exception.
    // -------------------------------------------------------------------------

    [Fact]
    public void OlderBlob_WithoutFingerprintFields_ParsesAsNull()
    {
        // Hand-craft a blob that looks like a v3 blob (no fpst/fpct/fpda keys).
        const string v3Blob = "{\"v\":3,\"bpm\":128.0,\"kpc\":0,\"kmd\":\"maj\",\"nrg\":7}";

        var restored = AnalysisStateCodec.TryParse(v3Blob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.Fingerprint);
        Assert.Equal(128.0, restored.Detected.Bpm);
    }

    // -------------------------------------------------------------------------
    // 5. Blob with partial fingerprint fields (only fpst, no fpct) → Fingerprint null
    // -------------------------------------------------------------------------

    [Fact]
    public void PartialFingerprintFields_GiveNullFingerprint()
    {
        // fpst present but fpct absent — should not reconstruct a partial fingerprint.
        const string partialBlob =
            "{\"v\":4,\"bpm\":128.0,\"fpst\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==\"}";

        var restored = AnalysisStateCodec.TryParse(partialBlob);

        // Must not throw; Fingerprint is null (both fpst AND fpct must be present).
        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.Fingerprint);
    }

    // -------------------------------------------------------------------------
    // 6. Version bump: CurrentVersion is 6 (v6 added RhythmPattern)
    // -------------------------------------------------------------------------

    [Fact]
    public void CurrentVersion_Is_Nine()
    {
        Assert.Equal(9, TrackAnalysisState.CurrentVersion);
    }

    // -------------------------------------------------------------------------
    // 7. Blob size is compact — fingerprint adds at most ~600 base64 chars
    // -------------------------------------------------------------------------

    [Fact]
    public void BlobWithFingerprint_IsCompact()
    {
        var fp    = MakeFingerprint();
        var state = MakeState(fp);
        var blob  = AnalysisStateCodec.Serialize(state);

        // ST:  64 floats × 4 bytes = 256 bytes → base64 ≈ 344 chars (+ JSON key + quotes ≈ 357)
        // CT:  24 floats × 4 bytes =  96 bytes → base64 ≈ 128 chars (+ JSON key + quotes ≈ 141)
        // DA:  float as JSON number ≈ 8-10 chars (+ key ≈ 17 chars)
        // JSON commas and structure: ~15 chars
        // Total delta budget: ≤ 550 chars is the theoretical minimum; we allow ≤ 700 in practice
        // to leave room for the JSON serialiser's formatting choices (extra whitespace-free but
        // still possible small overhead from the source-generated context).
        var noFpBlob = AnalysisStateCodec.Serialize(MakeState(null));
        var delta    = blob.Length - noFpBlob.Length;

        Assert.True(delta <= 700,
            $"Fingerprint added {delta} chars to the blob; expected ≤ 700. " +
            $"Blob size with FP: {blob.Length} chars, without: {noFpBlob.Length} chars.");
    }
}
