using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Core.Models;

/// <summary>
/// (De)serialises <see cref="TrackAnalysisState"/> to/from the compact JSON blob
/// stored in the file's JUSTPLAY tag. Source-generated (trim/AOT-safe — the repo
/// stays reflection-free). Keys are short to keep the tag small; the musical key is
/// stored losslessly as pitch-class + mode rather than a Camelot string so no
/// reverse parser is needed.
/// </summary>
public static class AnalysisStateCodec
{
    public static string Serialize(TrackAnalysisState state)
    {
        var k = state.Detected.Key;
        var o = state.Original;
        var ok = o?.Key;
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

            return new TrackAnalysisState
            {
                Version = dto.V,
                Detected = new AnalysisResult
                {
                    Bpm = dto.Bpm,
                    Key = key,
                    KeyConfidence = dto.KeyConf,
                    Energy = dto.Energy,
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
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnalysisStateDto))]
internal sealed partial class AnalysisStateJsonContext : JsonSerializerContext;
