using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>Reads tag-level metadata from a file (TagLib# today).</summary>
public interface IMetadataReader
{
    TrackMetadata Read(string filePath);

    /// <summary>
    /// Read only the editorial fields (title, artist, album, etc.) as an
    /// <see cref="EditableTags"/> snapshot. Suitable for pre-populating a tag editor.
    /// Degrades gracefully: returns an empty <see cref="EditableTags"/> on any error.
    /// </summary>
    EditableTags ReadEditable(string filePath);
}
