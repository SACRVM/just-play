using System.Globalization;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Metadata;

/// <summary>
/// Writes analysis values back via TagLib#. BPM → standard tempo tag (rounded; the
/// exact value is preserved in the JUSTPLAY blob), key → standard key tag as an ID3
/// key string (e.g. "Am"), energy + the JustPlay state blob → custom fields. Camelot
/// is intentionally NOT written (derived from the key on read) so the user's comment
/// is never clobbered.
/// </summary>
public sealed class TagLibMetadataWriter : IMetadataWriter
{
    private static readonly string[] PitchNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public void Write(string filePath, TagWrite write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var file = TagLib.File.Create(filePath);
        var tag = file.Tag;

        if (write.Bpm is { } bpm)
            tag.BeatsPerMinute = (uint)Math.Clamp(Math.Round(bpm), 0, 999);

        if (write.Key is { } key)
            tag.InitialKey = ToId3Key(key);

        if (write.Energy is { } energy)
            TagCustomFields.Set(file, "ENERGY", energy.ToString(CultureInfo.InvariantCulture));

        if (write.State is { } state)
            TagCustomFields.Set(file, "JUSTPLAY", AnalysisStateCodec.Serialize(state));

        file.Save();
    }

    /// <summary>MusicalKey → ID3v2 TKEY string ("A","C#","Am","F#m", …).</summary>
    private static string ToId3Key(MusicalKey key)
        => PitchNames[((key.PitchClass % 12) + 12) % 12] + (key.Mode == KeyMode.Minor ? "m" : "");
}
