using System;

namespace JustPlay.Metadata;

/// <summary>
/// Answers one cheap question: <b>does this file carry embedded artwork?</b> — without handing anyone
/// the bytes.
///
/// <para>It exists because the LIBRARY INDEX cannot answer it: the index stores tags, not pictures, so
/// a row filled from it knows nothing about a cover. Reporting "no cover" for such a row is a lie, and
/// it is the lie Chloe caught (2026-08-05: "es findet nur selten ART aber fast jeder song hat ein
/// cover"). So the answer is fetched from the FILE, on demand, only when something actually asks —
/// the same rule as the ID3 version.</para>
///
/// <para>The picture SELECTION is shared with <see cref="TagLibMetadataReader"/> rather than copied:
/// Serato and Mixed In Key put GEOB blobs into TagLib's <c>Pictures[]</c>, so "has a picture" is not
/// "Pictures.Length &gt; 0". Two copies of that rule would drift, and one of them would start counting
/// cue points as album art.</para>
/// </summary>
public static class CoverProbe
{
    /// <summary>True when the file carries real embedded artwork. False for anything unreadable —
    /// a locked or corrupt file is not a reason to fail, it just has no cover to report.</summary>
    public static bool Has(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            using var file = TagLib.File.Create(filePath);
            return Best(file.Tag) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The one real picture in a tag, or null. Prefers the designated FrontCover, then any genuine
    /// picture type, and only then falls back to an image-mime attachment that a tagger mislabelled.
    ///
    /// <para>⚠ The <c>NotAPicture</c> skip is the important line: Serato / Mixed In Key store their
    /// performance data (Key, Energy, Serato Markers2, CuePoints, BeatGrid, …) as GEOB frames, which
    /// TagLib# surfaces in <c>Pictures[]</c> with <c>Type=NotAPicture</c> and an application/json mime.
    /// On a MIK/Serato-processed file the real cover sits BEHIND up to six of them, so taking
    /// <c>Pictures[0]</c> hands the UI a JSON blob instead of the artwork.</para>
    /// </summary>
    internal static TagLib.IPicture? Best(TagLib.Tag tag)
    {
        var pics = tag.Pictures;
        if (pics is null || pics.Length == 0) return null;

        TagLib.IPicture? best = null;
        foreach (var p in pics)
        {
            if (p.Type == TagLib.PictureType.FrontCover) return p;
            best ??= p.Type != TagLib.PictureType.NotAPicture ? p : null;
        }

        if (best is not null) return best;

        // Last resort: an image-mime attachment mislabelled NotAPicture (some taggers).
        foreach (var p in pics)
        {
            if (p.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return p;
        }

        return null;
    }
}
