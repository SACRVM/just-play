using System.Text.Json.Serialization;

namespace JustPlay.Engine.Dtos;

/// <summary>
/// DSP analysis result for one track, returned by <see cref="ITrackEngine.AnalyzeTrackAsync"/>.
/// Matches the field set of <c>JustPlay.Core.Models.AnalysisResult</c> but expressed as a
/// flat, JSON-friendly record.  CoverArt bytes are excluded (too large for JSON transport;
/// use <see cref="ITrackEngine.ReadTagsAsync"/> for file reads).
/// JSON-serializable via <see cref="EngineJsonContext"/> (source-generated, trim/AOT-safe).
/// </summary>
public sealed record TrackAnalysisDto
{
    [JsonPropertyName("filePath")]       public required string FilePath       { get; init; }
    /// <summary>Detected BPM (after tempo-octave correction), or null if undetected.</summary>
    [JsonPropertyName("bpm")]            public double?  Bpm            { get; init; }
    /// <summary>Detected key in conventional form, e.g. "A minor", or null.</summary>
    [JsonPropertyName("keyName")]        public string?  KeyName        { get; init; }
    /// <summary>Camelot wheel code, e.g. "8A", or null.</summary>
    [JsonPropertyName("keyCamelot")]     public string?  KeyCamelot     { get; init; }
    /// <summary>Key detection confidence, 0..1, or null.</summary>
    [JsonPropertyName("keyConfidence")]  public double?  KeyConfidence  { get; init; }
    /// <summary>Perceived energy, 1..10 (DEAM-calibrated), or null.</summary>
    [JsonPropertyName("energy")]         public int?     Energy         { get; init; }
    /// <summary>DFA danceability scalar (from the beat fingerprint), or null when
    /// the track was too short or fingerprint extraction failed.</summary>
    [JsonPropertyName("danceability")]   public float?   Danceability   { get; init; }
    /// <summary>Whether the analysis succeeded (false when all fields are null).</summary>
    [JsonPropertyName("success")]        public bool     Success        { get; init; }
    /// <summary>Human-readable failure message, or null on success.</summary>
    [JsonPropertyName("error")]          public string?  Error          { get; init; }
}
