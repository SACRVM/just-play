using System.Text.Json.Serialization;
using JustPlay.Engine.Dtos;

namespace JustPlay.Engine;

/// <summary>
/// Source-generated JSON serialization context for all <c>JustPlay.Engine</c> DTOs.
/// Reflection-free and trim/AOT-safe - the same approach used by
/// <c>AnalysisStateJsonContext</c> in JustPlay.Core and <c>StreamingJsonContext</c>.
///
/// <para>Usage (serialize):</para>
/// <code>
/// var json = JsonSerializer.Serialize(dto, EngineJsonContext.Default.TrackAnalysisDto);
/// </code>
/// <para>Usage (deserialize):</para>
/// <code>
/// var req = JsonSerializer.Deserialize(json, EngineJsonContext.Default.WriteTagsRequest);
/// </code>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrackTagsDto))]
[JsonSerializable(typeof(TrackAnalysisDto))]
[JsonSerializable(typeof(WriteTagsRequest))]
[JsonSerializable(typeof(WriteTagsResult))]
[JsonSerializable(typeof(LibraryAnalysisResult))]
[JsonSerializable(typeof(IReadOnlyList<TrackAnalysisDto>))]
[JsonSerializable(typeof(List<TrackAnalysisDto>))]
public sealed partial class EngineJsonContext : JsonSerializerContext;
