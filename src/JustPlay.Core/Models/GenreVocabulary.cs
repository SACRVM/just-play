using System;
using System.Collections.Generic;
using System.Linq;

namespace JustPlay.Core.Models;

/// <summary>
/// The fallback genre list the tag editor suggests from when the library index has nothing to offer
/// (a fresh install, or a genre nobody in the library uses yet).
///
/// <para>Two halves, on purpose. The <b>ID3v1 set</b> is the interop baseline - those 80 names are
/// what every other tagger, car stereo and old DJ deck knows, so typing one of them is the safest
/// thing you can write into a file. The <b>dance/DJ supplement</b> is the vocabulary an ID3v1 list
/// from 1998 simply does not have: it predates almost everything played in a club today.</para>
///
/// <para>(!!) This is a suggestion list, never a restriction. A genre that is not in here is not
/// wrong - the editor must always accept free text. The list exists to stop the same style being
/// spelled four ways across a library, not to police what a style is.</para>
/// </summary>
public static class GenreVocabulary
{
    /// <summary>The ID3v1 genre table (indices 0-79) - the names other software recognises.</summary>
    public static readonly IReadOnlyList<string> Id3v1 =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop",
        "Jazz", "Metal", "New Age", "Oldies", "Other", "Pop", "R&B", "Rap", "Reggae", "Rock",
        "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks", "Soundtrack",
        "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion", "Trance",
        "Classical", "Instrumental", "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise",
        "Alternative Rock", "Bass", "Soul", "Punk", "Space", "Meditative", "Instrumental Pop",
        "Instrumental Rock", "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic",
        "Pop-Folk", "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta",
        "Top 40", "Christian Rap", "Pop/Funk", "Jungle", "Native American", "Cabaret",
        "New Wave", "Psychedelic", "Rave", "Showtunes", "Trailer", "Lo-Fi", "Tribal",
        "Acid Punk", "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
    ];

    /// <summary>
    /// The styles a 1998 list cannot know. Kept deliberately at the level DJs actually file by -
    /// "Hard Techno" and "Bass House" are useful, one-off micro-genres are not: a suggestion list
    /// only helps if the same track lands under the same name next time.
    /// </summary>
    public static readonly IReadOnlyList<string> Dance =
    [
        // House and its neighbours
        "Deep House", "Tech House", "Bass House", "Progressive House", "Electro House",
        "Future House", "Melodic House", "Afro House", "Amapiano", "Disco House", "Funky House",
        "Jackin' House", "Slap House", "Vinahouse",
        // Techno and harder
        "Hard Techno", "Melodic Techno", "Minimal", "Acid Techno", "Industrial Techno",
        "Schranz", "Hardgroove",
        // Trance
        "Progressive Trance", "Uplifting Trance", "Psytrance", "Hard Trance", "Tech Trance",
        // Hard dance
        "Hardstyle", "Rawstyle", "Hardcore", "Uptempo", "Frenchcore", "Gabber", "Happy Hardcore",
        "Jumpstyle", "Hard Dance",
        // Bass and breaks
        "Drum & Bass", "Liquid DnB", "Neurofunk", "Jump Up", "Dubstep", "Riddim", "Breakbeat",
        "UK Garage", "2-Step", "Speed Garage", "Bassline", "Jersey Club", "Baile Funk",
        "Footwork", "Breaks",
        // Everything else that shows up in a set
        "EDM", "Big Room", "Future Bass", "Trap", "Hip Hop", "Drill", "Afrobeats", "Reggaeton",
        "Dancehall", "Moombahton", "Nu Disco", "Italo Disco", "Synthwave", "Downtempo",
        "Chillout", "Lo-Fi Hip Hop", "Hardwave", "Phonk", "Eurobeat", "Hands Up",
    ];

    /// <summary>Both halves, de-duplicated and sorted - what an editor offers with no library to
    /// learn from. Case-insensitive de-dup, because "Hip-Hop" and "Hip Hop" both being offered
    /// would defeat the point of suggesting anything.</summary>
    public static readonly IReadOnlyList<string> Default =
        Id3v1.Concat(Dance)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
             .ToArray();

    /// <summary>
    /// Merge what the library actually contains with the fallback list. The library's own genres
    /// come FIRST and win any case collision - the spelling already in her files is the right one,
    /// whatever this list thinks.
    /// </summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string>? fromLibrary)
    {
        if (fromLibrary is null) return Default;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var g in fromLibrary)
        {
            if (string.IsNullOrWhiteSpace(g)) continue;
            var name = g.Trim();
            if (seen.Add(name)) merged.Add(name);
        }

        foreach (var g in Default)
            if (seen.Add(g)) merged.Add(g);

        merged.Sort(StringComparer.OrdinalIgnoreCase);
        return merged;
    }
}
