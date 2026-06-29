using System.Text.Json.Serialization;
using JustPlay.Cli.Index;
using JustPlay.Cli.Reports;

namespace JustPlay.Cli;

/// <summary>
/// Source-generated JSON serialization context for all CLI-specific types.
/// Reflection-free and trim/AOT-safe — consistent with EngineJsonContext.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScanReport))]
[JsonSerializable(typeof(DedupReport))]
[JsonSerializable(typeof(DedupGroup))]
[JsonSerializable(typeof(List<DedupGroup>))]
[JsonSerializable(typeof(TrackIndexEntry))]
[JsonSerializable(typeof(TrackIndex))]
[JsonSerializable(typeof(StatsReport))]
[JsonSerializable(typeof(BpmBucket))]
[JsonSerializable(typeof(List<BpmBucket>))]
[JsonSerializable(typeof(SqueezeReport))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
public sealed partial class CliJsonContext : JsonSerializerContext;
