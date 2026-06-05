using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Core.Models;

/// <summary>
/// (De)serialises <see cref="TrackAnalysisState"/> to/from the compact JSON blob
/// stored in the file's JUSTPLAY tag. Source-generated (trim/AOT-safe — the repo
/// stays reflection-free). Keys are short to keep the tag small; the musical key is
/// stored losslessly as pitch-class + mode rather than a Camelot string so no
/// reverse parser is needed.
///
/// <para><b>Fingerprint encoding (v4+):</b></para>
/// The beat fingerprint (64 ST floats + 24 CT floats + 1 DA float) is stored as two
/// base64 strings ("fpst", "fpct") plus a single float ("fpda").  Each float array
/// is encoded as a little-endian raw binary byte array → base64, matching System.Buffers
/// conventions and keeping the tag compact (~120 chars for ST, ~44 for CT vs ~400+ for
/// JSON float arrays with decimal notation).
/// </summary>
public static class AnalysisStateCodec
{
    public static string Serialize(TrackAnalysisState state)
    {
        var k = state.Detected.Key;
        var o = state.Original;
        var ok = o?.Key;
        var fp = state.Detected.Fingerprint;
        var dto = new AnalysisStateDto
        {
            V = state.Version,
            Bpm = state.Detected.Bpm,
            KeyPc = k?.PitchClass,
            KeyMode = k is { } key ? (key.Mode == KeyMode.Major ? "maj" : "min") : null,
            KeyConf = state.Detected.KeyConfidence,
            Energy = state.Detected.Energy,
            ActBpm = Code(state.BpmDecision),
            ActKey = Code(state.KeyDecision),
            ActEnergy = Code(state.EnergyDecision),
            // Original (pre-overwrite foreign values) — only emitted when present.
            OrigBpm = o?.Bpm,
            OrigKeyPc = ok?.PitchClass,
            OrigKeyMode = ok is { } okey ? (okey.Mode == KeyMode.Major ? "maj" : "min") : null,
            OrigEnergy = o?.Energy,
            // Beat fingerprint — base64-encoded little-endian float arrays (v4+).
            FpSt = fp is not null ? FloatsToBase64(fp.ScaleTransform)  : null,
            FpCt = fp is not null ? FloatsToBase64(fp.CyclicTempogram) : null,
            FpDa = fp?.Danceability,
            // Loudness / ReplayGain (v5+).
            Lufs = state.Detected.LoudnessLufs,
            Rg   = state.Detected.ReplayGainDb,
            Pk   = state.Detected.Peak,
        };
        return JsonSerializer.Serialize(dto, AnalysisStateJsonContext.Default.AnalysisStateDto);
    }

    /// <summary>Parse the blob, or null if absent/corrupt (caller then treats the track as un-analysed).</summary>
    public static TrackAnalysisState? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize(json, AnalysisStateJsonContext.Default.AnalysisStateDto);
            if (dto is null) return null;

            MusicalKey? key = dto.KeyPc is int pc
                ? new MusicalKey(pc, dto.KeyMode == "maj" ? KeyMode.Major : KeyMode.Minor)
                : null;

            MusicalKey? origKey = dto.OrigKeyPc is int opc
                ? new MusicalKey(opc, dto.OrigKeyMode == "maj" ? KeyMode.Major : KeyMode.Minor)
                : null;

            // Original is present only if at least one foreign field was stashed.
            AnalysisResult? original =
                dto.OrigBpm is null && origKey is null && dto.OrigEnergy is null
                    ? null
                    : new AnalysisResult { Bpm = dto.OrigBpm, Key = origKey, Energy = dto.OrigEnergy };

            // Beat fingerprint — decode from base64 if all three fields are present.
            BeatFingerprint? fingerprint = null;
            if (dto.FpSt is { } fpStB64 && dto.FpCt is { } fpCtB64 && dto.FpDa is { } fpDa)
            {
                var st = TryBase64ToFloats(fpStB64);
                var ct = TryBase64ToFloats(fpCtB64);
                if (st is not null && ct is not null)
                    fingerprint = new BeatFingerprint(st, ct, fpDa);
            }

            return new TrackAnalysisState
            {
                Version = dto.V,
                Detected = new AnalysisResult
                {
                    Bpm = dto.Bpm,
                    Key = key,
                    KeyConfidence = dto.KeyConf,
                    Energy = dto.Energy,
                    Fingerprint = fingerprint,
                    LoudnessLufs = dto.Lufs,
                    ReplayGainDb = dto.Rg,
                    Peak = dto.Pk,
                },
                Original = original,
                BpmDecision = Decode(dto.ActBpm),
                KeyDecision = Decode(dto.ActKey),
                EnergyDecision = Decode(dto.ActEnergy),
            };
        }
        catch (JsonException)
        {
            return null; // foreign / malformed blob — treat as no state
        }
    }

    // -------------------------------------------------------------------------
    // Float array ↔ base64 helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Encodes a float array as a base64 string of its little-endian binary representation.
    /// Compact: 89 floats (ST 64 + CT 24 + DA 1) = 356 bytes → 476 base64 chars vs ~600+
    /// chars of JSON decimal notation with full precision.
    /// </summary>
    private static string FloatsToBase64(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        for (var i = 0; i < floats.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), floats[i]);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decodes a base64 string back to a float array.
    /// Returns null if the string is malformed or has an unexpected length.
    /// </summary>
    private static float[]? TryBase64ToFloats(string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64);
            if (bytes.Length % 4 != 0) return null;
            var floats = new float[bytes.Length / 4];
            for (var i = 0; i < floats.Length; i++)
                floats[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
            return floats;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Code(FieldDecision d) => d switch
    {
        FieldDecision.Applied => "A",
        FieldDecision.Kept => "K",
        _ => "P",
    };

    private static FieldDecision Decode(string? c) => c switch
    {
        "A" => FieldDecision.Applied,
        "K" => FieldDecision.Kept,
        _ => FieldDecision.Pending,
    };
}

/// <summary>Wire shape of the JUSTPLAY blob. Short keys keep the embedded tag small.</summary>
internal sealed class AnalysisStateDto
{
    [JsonPropertyName("v")]   public int V { get; set; }
    [JsonPropertyName("bpm")] public double? Bpm { get; set; }
    [JsonPropertyName("kpc")] public int? KeyPc { get; set; }
    [JsonPropertyName("kmd")] public string? KeyMode { get; set; }
    [JsonPropertyName("kcf")] public double? KeyConf { get; set; }
    [JsonPropertyName("nrg")] public int? Energy { get; set; }
    [JsonPropertyName("abpm")] public string? ActBpm { get; set; }
    [JsonPropertyName("akey")] public string? ActKey { get; set; }
    [JsonPropertyName("anrg")] public string? ActEnergy { get; set; }
    // Original (pre-overwrite foreign) values, for reversibility. Omitted when null.
    [JsonPropertyName("obpm")] public double? OrigBpm { get; set; }
    [JsonPropertyName("okpc")] public int? OrigKeyPc { get; set; }
    [JsonPropertyName("okmd")] public string? OrigKeyMode { get; set; }
    [JsonPropertyName("onrg")] public int? OrigEnergy { get; set; }
    // Beat fingerprint (v4+). Base64-encoded little-endian float arrays.
    // "fpst" = Scale Transform (64 floats), "fpct" = Cyclic Tempogram (24 floats),
    // "fpda" = DFA danceability scalar. All three must be present to reconstruct.
    [JsonPropertyName("fpst")] public string? FpSt { get; set; }
    [JsonPropertyName("fpct")] public string? FpCt { get; set; }
    [JsonPropertyName("fpda")] public float?  FpDa { get; set; }
    // Loudness / ReplayGain (v5+).
    [JsonPropertyName("lufs")] public double? Lufs { get; set; }
    [JsonPropertyName("rg")]   public double? Rg   { get; set; }
    [JsonPropertyName("pk")]   public double? Pk   { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnalysisStateDto))]
internal sealed partial class AnalysisStateJsonContext : JsonSerializerContext;
