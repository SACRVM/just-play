using System.Text.Json.Serialization;

namespace JustPlay.Library;

/// <summary>
/// Source-generated JSON serialization context for the library index.
/// Reflection-free and trim/AOT-safe - same philosophy as EngineJsonContext and CliJsonContext.
///
/// <para><see cref="JsonSourceGenerationOptions"/> mirrors the CLI's context exactly (indented,
/// case-insensitive, nulls omitted) so an index written by the app is byte-comparable with one
/// written by <c>JustPlayCLI analyze</c>.</para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrackIndex))]
[JsonSerializable(typeof(TrackIndexEntry))]
public sealed partial class LibraryJsonContext : JsonSerializerContext;
