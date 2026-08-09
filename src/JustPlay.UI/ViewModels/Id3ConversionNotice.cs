using System;
using System.Globalization;

namespace JustPlay.UI.ViewModels;

/// <summary>
/// ONE voice for the ID3-version conversion warning, wherever the suite has to give it.
///
/// <para><b>The fact.</b> Saving an MP3 while the WRITE FORMAT setting is on one of the three CONVERT
/// options normalises the file to that version - in BOTH directions, including a v2.4 file being pulled
/// down to v2.3. Measured 2026-07-31 (128 writes / 787 vendor frames): the conversion re-serialises the
/// whole tag and re-encodes GEOB <i>descriptors</i>. Not one vendor byte is lost - but Serato and Mixed
/// In Key look their cue points, key and energy up BY that descriptor string, so the data can become
/// unfindable while nothing looks broken.</para>
///
/// <para><b>Why it is a class and not two string literals.</b> Two surfaces state this fact - the tag
/// editor, above Save, about what is open; and the RAW TAGS window, about the file it is listing. They
/// are gated differently on purpose, but they must not be WORDED differently: one fact, one sentence.
/// The copy lived in JUST TAG's editor, was deleted when JUST TAG moved onto the Finder's shell, and
/// was then half re-typed in the raw-tags view model. It lives here now so that cannot happen a third
/// time.</para>
///
/// <para><b>Colour.</b> The warm-sand <c>Caution*</c> tokens and the <c>Border.caution</c> style
/// (JustPalette / JustStyles) - not red, because a convert setting is a legitimate choice and not an
/// error, and not the accent, which means "selected" in this design system.</para>
/// </summary>
public static class Id3ConversionNotice
{
    /// <summary>
    /// The name a version is called by in prose: <c>"ID3v2.4"</c>.
    ///
    /// <para>Accepts either shape the suite carries a version in, because both reach this notice: the
    /// SHORT form <c>"2.4"</c> that <c>TrackMetadata.Id3Version</c> / the library index / the ID3 column
    /// use, and the full container label <c>"ID3v2.4"</c> the raw-tag reader hands out. Both come from
    /// the same <c>Id3VersionProbe</c>, so this only ever adds the family name - it never decides what
    /// version anything is, and there is no second version table here to drift.</para>
    /// </summary>
    public static string Display(string version) =>
        version.StartsWith("ID3v", StringComparison.Ordinal) ? version : "ID3v" + version;

    /// <summary>The headline for ONE file: what it is now, and what a save would make it.</summary>
    public static string Headline(string fileVersion, string convertsTo) =>
        $"This file is {Display(fileVersion)}. Saving converts it to {Display(convertsTo)}.";

    /// <summary>
    /// The headline for a SELECTION. It names the target and counts the files that are not on it
    /// already, because a selection can hold several versions at once and listing them all would say
    /// less than the count does - the ID3 column beside it says which ones.
    /// </summary>
    public static string Headline(int affected, int total, string convertsTo)
    {
        var inv = CultureInfo.InvariantCulture;
        var target = Display(convertsTo);

        if (affected == total)
            return $"These {total.ToString(inv)} files are not {target}. Saving converts every one of them.";

        // One-of-many gets its own sentence rather than a plural with a "1" in it: a warning that
        // cannot count is a warning that gets read as boilerplate.
        return affected == 1
            ? $"1 of these {total.ToString(inv)} files is not {target}. Saving converts it."
            : $"{affected.ToString(inv)} of these {total.ToString(inv)} files are not {target}. " +
              "Saving converts them.";
    }

    /// <summary>The consequence. The same sentence on every surface that carries this warning.</summary>
    public const string Body =
        "Serato and Mixed In Key find their data by the frame label, and the conversion rewrites those " +
        "labels. The cue points stay in the file - those apps can stop finding them.";

    /// <summary>
    /// The way out, which is the same for every file - including an ID3v2.2 that no write format could
    /// reproduce: stop converting altogether.
    /// </summary>
    public static string Fix(bool many) =>
        (many ? "Keep them as they are: choose " : "Keep it as it is: choose ") +
        "\"Keep each file's version\" under WRITE FORMAT in Settings.";
}
