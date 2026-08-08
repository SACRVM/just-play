using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JustPlay.Core.Tagging;

/// <summary>
/// The mask language that connects a file's NAME and its TAGS, in both directions.
///
/// <para><c>%artist% - %title%</c> read one way builds a name out of tags; read the other way it
/// pulls tags out of a name. One definition for both, because they are one language: a placeholder
/// that means "artist" while renaming cannot be allowed to mean something else while parsing, and
/// two implementations is how that happens.</para>
///
/// <para>Platform-agnostic and unit-tested on purpose. Parsing a name is the half that can be
/// WRONG - it guesses, and the guess is then written into hundreds of files - so it is arithmetic to
/// be tested rather than behaviour to be eyeballed in a window.</para>
/// </summary>
public static class FileNameMask
{
    /// <summary>A placeholder that matches text and throws it away - the track number in
    /// "01 - Artist - Title" when you do not want it. mp3tag's convention, kept.</summary>
    public const string Discard = "dummy";

    /// <summary>Every placeholder the language knows, without the percent signs.</summary>
    public static readonly IReadOnlyList<string> Fields =
    [
        "artist", "title", "album", "albumartist", "genre", "year", "track",
    ];

    /// <summary>
    /// The placeholders <paramref name="mask"/> actually uses, in the order they appear. Duplicates
    /// and unknown names are left out; <see cref="Discard"/> never appears, because it names nothing.
    /// </summary>
    public static IReadOnlyList<string> FieldsIn(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask)) return [];

        var seen = new List<string>();
        foreach (Match m in Placeholder.Matches(mask))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (name == Discard || !Fields.Contains(name) || seen.Contains(name)) continue;
            seen.Add(name);
        }
        return seen;
    }

    /// <summary>
    /// Build a name from tag values - the RENAME direction. A placeholder with no value collapses,
    /// and the caller tidies up whatever separators that leaves behind.
    /// </summary>
    public static string Format(string mask, Func<string, string?> value)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(value);

        return Placeholder.Replace(mask, m =>
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            return name == Discard ? "" : value(name) ?? "";
        });
    }

    /// <summary>
    /// Read tag values OUT of a name - the direction that guesses. Returns null when the name does
    /// not fit the mask at all, which is the answer that matters: a file that does not match must be
    /// left alone rather than filled with whatever a loose pattern happened to capture.
    /// </summary>
    /// <param name="mask">e.g. <c>%artist% - %title%</c>.</param>
    /// <param name="nameWithoutExtension">The file name, extension already removed.</param>
    public static IReadOnlyDictionary<string, string>? Parse(string mask, string nameWithoutExtension)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(nameWithoutExtension);

        var subject = nameWithoutExtension.Trim();

        // TWO PASSES, strict first. A tolerant pattern alone would be worse than a strict one, not
        // better: with "-" allowed to mean "any dash, spaces optional", "AC-DC - Highway" parses as
        // artist "AC" - a name that the strict pattern gets right, broken by the very leniency meant
        // to help. So the exact form gets first refusal, and only a name that does not fit AT ALL is
        // offered the loose one, where the alternative was no answer anyway.
        var match = ToRegex(mask, tolerant: false)?.Match(subject);
        if (match is not { Success: true })
            match = ToRegex(mask, tolerant: true)?.Match(subject);

        if (match is not { Success: true }) return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Fields)
        {
            var g = match.Groups[name];
            if (!g.Success) continue;

            var v = g.Value.Trim();
            if (v.Length > 0) result[name] = v;
        }

        return result;
    }

    /// <summary>Does this mask stand a chance of parsing anything? A mask of pure literals matches
    /// exactly one name and fills nothing, which is not an instruction anybody means.</summary>
    public static bool CanParse(string? mask) =>
        FieldsIn(mask).Count > 0 && ToRegex(mask!, tolerant: false) is not null;

    /// <summary>
    /// How many PATH SEGMENTS a mask reaches over. <c>%album%/%track% - %title%</c> is 2: the file
    /// name and the folder holding it.
    /// <para>The folder IS data. A library filed as <c>.../Hard Techno/Perc - Gob.mp3</c> keeps the
    /// genre in the path and nowhere else, and a mask that can only see the file name cannot get at
    /// it.</para>
    /// </summary>
    public static int SegmentCount(string? mask) =>
        string.IsNullOrEmpty(mask) ? 1 : mask.Count(c => c is '/' or '\\') + 1;

    /// <summary>
    /// The part of <paramref name="fullPath"/> a mask of this shape should be matched against: the
    /// file name without its extension, preceded by as many parent folders as the mask asks for,
    /// joined with <c>/</c> so a mask can be written either way round.
    /// </summary>
    public static string SubjectFor(string mask, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        var segments = SegmentCount(mask);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (segments <= 1) return name;

        var parts = new List<string> { name };
        var dir = Path.GetDirectoryName(fullPath);
        while (parts.Count < segments && !string.IsNullOrEmpty(dir))
        {
            var leaf = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(leaf)) break;      // a drive/share root has no name to use
            parts.Insert(0, leaf);
            dir = Path.GetDirectoryName(dir);
        }

        return string.Join('/', parts);
    }

    // Character codes, not escapes: this file is ASCII by rule, and 0x2013 states which dash is
    // meant more plainly than counting backslashes inside a string literal does.
    private const char EnDash = (char)0x2013;
    private const char EmDash = (char)0x2014;
    private const char Backslash = (char)0x5C;

    private static readonly Regex Placeholder =
        new(@"%([a-zA-Z]+)%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<(string, bool), Regex?> Cache = new();

    /// <summary>
    /// Turn the mask into an anchored regex with one named group per placeholder.
    ///
    /// <para>Every group is LAZY except the last, which is greedy. That is what makes
    /// <c>%artist% - %title%</c> read "A - B - C" as artist "A" and title "B - C" rather than the
    /// other way round: the separator people put between two fields is usually also inside the
    /// second one, so the FIRST match of it is the split, and everything after it belongs to the
    /// last field. Same rule mp3tag follows, and the one that matches how people name files.</para>
    ///
    /// <para>Returns null for a mask that cannot work: no placeholders at all, or the same one
    /// twice (which would be two different answers for one field).</para>
    /// </summary>
    private static Regex? ToRegex(string mask, bool tolerant)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((mask, tolerant), out var cached)) return cached;
            var built = Build(mask, tolerant);
            Cache[(mask, tolerant)] = built;
            return built;
        }
    }

    /// <summary>
    /// Turn a LITERAL run of the mask into a pattern.
    ///
    /// <para>Exact by default. In the tolerant pass three things loosen, and only these three,
    /// because they are the ways one naming scheme is written by different tools rather than
    /// different schemes: a hyphen also means an EN or EM dash; a space also means an underscore
    /// (and vice versa); and the run of spaces around a dash may be absent. Everything else -
    /// brackets, dots, the words in the mask - stays exact, because those ARE the scheme.</para>
    /// </summary>
    private static string Literal(string text, bool tolerant)
    {
        if (!tolerant) return Regex.Escape(text);

        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c) || c == '_')
            {
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '_')) i++;
                // Zero-or-more, so "Perc-Gob" still fits a mask written "%artist% - %title%".
                sb.Append(@"[\s_]*");
                continue;
            }

            // The dash family, and the folder slash - written as CHARACTER CODES. Source in this
            // repo is ASCII by rule (SourceIsAsciiTests), so an EN dash cannot appear as itself,
            // and a code point says which character is meant without anyone counting slashes.
            if (c is '-' or EnDash or EmDash)
            {
                sb.Append('[').Append('-').Append(EnDash).Append(EmDash).Append(']');
                i++;
                continue;
            }

            // Either slash in the mask means the same thing: a folder boundary. SubjectFor joins
            // with "/", so the mask may be typed with whichever separator is to hand.
            if (c is '/' or Backslash) { sb.Append('/'); i++; continue; }

            sb.Append(Regex.Escape(c.ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static Regex? Build(string mask, bool tolerant)
    {
        var matches = Placeholder.Matches(mask);
        if (matches.Count == 0) return null;

        var used = new HashSet<string>(StringComparer.Ordinal);
        var pattern = new StringBuilder("^");
        var at = 0;
        var placeholders = 0;

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var name = m.Groups[1].Value.ToLowerInvariant();
            var known = name == Discard || Fields.Contains(name);

            // An unknown placeholder is a typo, and a typo must not quietly become a literal that
            // then fails to match anything: refuse the mask instead.
            if (!known) return null;
            if (name != Discard && !used.Add(name)) return null;

            pattern.Append(Literal(mask[at..m.Index], tolerant));

            var last = i == matches.Count - 1;
            var body = last ? "(.+)" : "(.+?)";
            pattern.Append(name == Discard ? body : $"(?<{name}>{(last ? ".+" : ".+?")})");

            at = m.Index + m.Length;
            placeholders++;
        }

        pattern.Append(Literal(mask[at..], tolerant));
        pattern.Append('$');

        if (placeholders == 0) return null;

        try
        {
            return new Regex(pattern.ToString(),
                             RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }
        catch (ArgumentException)
        {
            return null;   // a mask that will not compile is a mask that does nothing, not a crash
        }
    }
}
