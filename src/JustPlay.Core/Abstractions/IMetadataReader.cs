using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>Reads tag-level metadata from a file (TagLib# today).</summary>
public interface IMetadataReader
{
    TrackMetadata Read(string filePath);
}
