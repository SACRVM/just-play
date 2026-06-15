using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Verifies that the vibe-quartet fields (v8+: punch/groove/dark/hypnotic + harshness/flatness)
/// round-trip correctly through <see cref="AnalysisStateCodec"/>.
///
/// Also checks:
/// - Null vibe fields round-trip as null (backwards-compatible with older blobs).
/// - A hand-crafted v7 blob (has "chr" key, no dark/hypnot keys) parses cleanly
///   with Dark == null + Hypnotic == null (unknown-field tolerance + graceful null defaults).
/// - CurrentVersion is now 8.
/// </summary>
public class CharacterCodecTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static TrackAnalysisState MakeState(
        double? rawScore   = 0.62,
        double? flatness   = 0.18,
        double? harshness  = 0.42,
        double? bassPunch  = 0.71,
        double? bassGroove = 0.14,
        double? dark       = 0.65,
        double? hypnotic   = 0.80) =>
        new()
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm              = 128.0,
                Energy           = 7,
                RawEnergyScore   = rawScore,
                SpectralFlatness = flatness,
                Harshness        = harshness,
                BassPunch        = bassPunch,
                BassGroove       = bassGroove,
                Dark             = dark,
                Hypnotic         = hypnotic,
            },
        };

    // =========================================================================
    // 1. All vibe-quartet scalars survive a round-trip
    // =========================================================================

    [Fact]
    public void VibeQuartet_Scalars_RoundTrip_WithPrecision()
    {
        var state    = MakeState(0.621, 0.183, 0.427, 0.715, 0.144, 0.683, 0.792);
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        var d = restored!.Detected;
        Assert.Equal(0.621, d.RawEnergyScore!.Value,   precision: 5);
        Assert.Equal(0.183, d.SpectralFlatness!.Value,  precision: 5);
        Assert.Equal(0.427, d.Harshness!.Value,         precision: 5);
        Assert.Equal(0.715, d.BassPunch!.Value,         precision: 5);
        Assert.Equal(0.144, d.BassGroove!.Value,        precision: 5);
        Assert.Equal(0.683, d.Dark!.Value,              precision: 5);
        Assert.Equal(0.792, d.Hypnotic!.Value,          precision: 5);
    }

    // =========================================================================
    // 2. Null vibe fields round-trip as null
    // =========================================================================

    [Fact]
    public void NullVibeFields_RoundTrip_AsNull()
    {
        var state    = MakeState(null, null, null, null, null, null, null);
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        var d = restored!.Detected;
        Assert.Null(d.RawEnergyScore);
        Assert.Null(d.SpectralFlatness);
        Assert.Null(d.Harshness);
        Assert.Null(d.BassPunch);
        Assert.Null(d.BassGroove);
        Assert.Null(d.Dark);
        Assert.Null(d.Hypnotic);
    }

    // =========================================================================
    // 3. v7 blob (has "chr" but no "dark"/"hypnot") parses cleanly
    //    Dark + Hypnotic come out null; "chr" is silently ignored.
    // =========================================================================

    [Fact]
    public void V7Blob_WithCharField_ParsesCleanly_DarkAndHypnoticAreNull()
    {
        // Hand-crafted v7 blob — has "chr" (now dropped), no dark/hypnot keys.
        const string v7Blob = "{\"v\":7,\"bpm\":128.0,\"kpc\":0,\"kmd\":\"maj\",\"nrg\":7," +
                              "\"lufs\":-9.5,\"rg\":8.5,\"pk\":0.988," +
                              "\"rp_fof\":0.90,\"rp_obe\":0.20,\"rp_swg\":0.10," +
                              "\"rp_syn\":0.10,\"rp_htf\":0.10,\"rp_bt\":\"4x4-driving\"," +
                              "\"raw_nrg\":0.62,\"sp_flat\":0.18,\"harsh\":0.42," +
                              "\"bs_pnch\":0.71,\"bs_grv\":0.14,\"chr\":\"punchy\"}";

        var restored = AnalysisStateCodec.TryParse(v7Blob);

        Assert.NotNull(restored);
        // Dark and Hypnotic must be null — they weren't in the v7 blob.
        Assert.Null(restored!.Detected.Dark);
        Assert.Null(restored.Detected.Hypnotic);
        // Other fields must be intact.
        Assert.Equal(128.0, restored.Detected.Bpm);
        Assert.Equal(7, restored.Detected.Energy);
        Assert.Equal(0.62, restored.Detected.RawEnergyScore!.Value, precision: 5);
        Assert.Equal(0.71, restored.Detected.BassPunch!.Value, precision: 5);
        Assert.NotNull(restored.Detected.Rhythm);
        Assert.Equal("4x4-driving", restored.Detected.Rhythm!.BeatType);
        // "chr" was silently ignored — no Character property on AnalysisResult.
    }

    // =========================================================================
    // 4. v6 blob (no character keys at all) parses cleanly — all vibe fields null
    // =========================================================================

    [Fact]
    public void V6Blob_WithoutVibeFields_ParsesAsNull()
    {
        const string v6Blob = "{\"v\":6,\"bpm\":128.0,\"kpc\":0,\"kmd\":\"maj\",\"nrg\":7," +
                              "\"lufs\":-9.5,\"rg\":8.5,\"pk\":0.988," +
                              "\"rp_fof\":0.90,\"rp_obe\":0.20,\"rp_swg\":0.10," +
                              "\"rp_syn\":0.10,\"rp_htf\":0.10,\"rp_bt\":\"4x4-driving\"}";

        var restored = AnalysisStateCodec.TryParse(v6Blob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.Dark);
        Assert.Null(restored.Detected.Hypnotic);
        Assert.Null(restored.Detected.RawEnergyScore);
        // Other fields intact.
        Assert.Equal(128.0, restored.Detected.Bpm);
        Assert.Equal(7, restored.Detected.Energy);
        Assert.NotNull(restored.Detected.Rhythm);
    }

    // =========================================================================
    // 5. Other fields unaffected when vibe quartet is present
    // =========================================================================

    [Fact]
    public void OtherFields_Preserved_WhenVibeQuartetPresent()
    {
        var state = new TrackAnalysisState
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm              = 127.5,
                Key              = new MusicalKey(3, KeyMode.Minor),
                KeyConfidence    = 0.88,
                Energy           = 8,
                LoudnessLufs     = -10.5,
                RawEnergyScore   = 0.70,
                Dark             = 0.55,
                Hypnotic         = 0.82,
            },
            BpmDecision = FieldDecision.Applied,
        };

        var restored = AnalysisStateCodec.TryParse(AnalysisStateCodec.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(127.5, restored!.Detected.Bpm);
        Assert.Equal(new MusicalKey(3, KeyMode.Minor), restored.Detected.Key);
        Assert.Equal(0.88, restored.Detected.KeyConfidence!.Value, precision: 5);
        Assert.Equal(8, restored.Detected.Energy);
        Assert.Equal(-10.5, restored.Detected.LoudnessLufs);
        Assert.Equal(0.70, restored.Detected.RawEnergyScore!.Value, precision: 5);
        Assert.Equal(0.55, restored.Detected.Dark!.Value, precision: 5);
        Assert.Equal(0.82, restored.Detected.Hypnotic!.Value, precision: 5);
    }

    // =========================================================================
    // 6. CurrentVersion is now 9
    // =========================================================================

    [Fact]
    public void CurrentVersion_Is_Nine()
    {
        Assert.Equal(9, TrackAnalysisState.CurrentVersion);
    }

    // =========================================================================
    // 7. Blob overhead is reasonable — vibe quartet adds at most ~200 chars
    // =========================================================================

    [Fact]
    public void BlobWithVibeQuartet_IsCompact()
    {
        var withVibe    = MakeState();
        var withoutVibe = MakeState(null, null, null, null, null, null, null);
        var blobWith    = AnalysisStateCodec.Serialize(withVibe);
        var blobWithout = AnalysisStateCodec.Serialize(withoutVibe);
        var delta       = blobWith.Length - blobWithout.Length;

        // 7 fields × ~20 chars avg ≈ 140; allow 260 to be generous.
        Assert.True(delta <= 260,
            $"Vibe quartet added {delta} chars to blob (expected ≤ 260). " +
            $"With: {blobWith.Length}, without: {blobWithout.Length}.");
    }

    // =========================================================================
    // 8. AcfSharpness + GridConfidence round-trip (v9+)
    // =========================================================================

    [Fact]
    public void AcfSharpnessAndGridConfidence_RoundTrip()
    {
        var state = new TrackAnalysisState
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult
            {
                Bpm           = 128.0,
                AcfSharpness  = 0.78,
                GridConfidence = 0.62,
            },
        };
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        Assert.Equal(0.78, restored!.Detected.AcfSharpness!.Value, precision: 5);
        Assert.Equal(0.62, restored.Detected.GridConfidence!.Value, precision: 5);
    }

    [Fact]
    public void NullAcfSharpnessAndGridConfidence_RoundTrip_AsNull()
    {
        var state = new TrackAnalysisState
        {
            Version  = TrackAnalysisState.CurrentVersion,
            Detected = new AnalysisResult { Bpm = 128.0 },
        };
        var blob     = AnalysisStateCodec.Serialize(state);
        var restored = AnalysisStateCodec.TryParse(blob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.AcfSharpness);
        Assert.Null(restored.Detected.GridConfidence);
    }

    [Fact]
    public void V8Blob_WithoutGridConfidenceFields_ParsesAsNull()
    {
        // Hand-crafted v8 blob — no acf_sh or gc keys.
        const string v8Blob = "{\"v\":8,\"bpm\":128.0,\"kpc\":0,\"kmd\":\"maj\",\"nrg\":7," +
                              "\"lufs\":-9.5,\"rg\":8.5,\"pk\":0.988," +
                              "\"rp_fof\":0.90,\"rp_obe\":0.20,\"rp_swg\":0.10," +
                              "\"rp_syn\":0.10,\"rp_htf\":0.10,\"rp_bt\":\"4x4-driving\"," +
                              "\"raw_nrg\":0.62,\"sp_flat\":0.18,\"harsh\":0.42," +
                              "\"bs_pnch\":0.71,\"bs_grv\":0.14,\"dark\":0.60,\"hypnot\":0.80}";

        var restored = AnalysisStateCodec.TryParse(v8Blob);

        Assert.NotNull(restored);
        Assert.Null(restored!.Detected.AcfSharpness);
        Assert.Null(restored.Detected.GridConfidence);
        // Other fields intact.
        Assert.Equal(128.0, restored.Detected.Bpm);
        Assert.Equal(0.80, restored.Detected.Hypnotic!.Value, precision: 5);
    }
}
