using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>Reads tag-level metadata from a file (TagLib# today).</summary>
public interface IMetadataReader
{
    TrackMetadata Read(string filePath);

    /// <summary>
    /// Read only the editorial fields (title, artist, album, etc.) as an
    /// <see cref="EditableTags"/> snapshot. Suitable for pre-populating a tag editor: a field that
    /// holds several values comes back as ALL of them in one string, never just the first, so what
    /// an editor shows is what the file holds.
    /// Degrades gracefully: returns an empty <see cref="EditableTags"/> on any error, with
    /// <see cref="EditableTags.ReadFailed"/> set when the file could not be read at all.
    /// </summary>
    EditableTags ReadEditable(string filePath);
}
