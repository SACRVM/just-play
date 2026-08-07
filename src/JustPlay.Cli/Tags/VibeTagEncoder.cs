using System.Globalization;
using System.Text;

namespace JustPlay.Cli.Tags;

/// <summary>
/// Encodes and decodes the JustPlay vibe tag string - a compact, pipe-delimited,
/// machine-parseable summary of the analysis results for use in Comment / Grouping tags.
///
/// <para>
/// <b>Wire format:</b><br/>
/// <c>JP[|E{energy}][|K{camelot}][|bpm{bpm_int}][|gc.{NN}][|gr.{NN}][|pu.{NN}][|hy.{NN}][|dk.{NN}][|hx.{NN}]</c>
/// </para>
///
/// <para>
/// <b>Field table:</b>
/// <list type="bullet">
///   <item><c>JP</c>       - required namespace prefix; marks this as a JustPlay tag.</item>
///   <item><c>E{N}</c>     - perceived energy 1-10 (e.g. <c>E7</c>).</item>
///   <item><c>K{c}</c>     - Camelot key (e.g. <c>K8A</c>, <c>K11B</c>).</item>
///   <item><c>bpm{N}</c>   - integer BPM, rounded (e.g. <c>bpm140</c>).</item>
///   <item><c>gc.{NN}</c>  - gridConfidence x 100, 2-digit zero-padded int 00-99 (e.g. <c>gc.57</c>).</item>
///   <item><c>gr.{NN}</c>  - bassGroove x 100 (e.g. <c>gr.14</c>).</item>
///   <item><c>pu.{NN}</c>  - bassPunch x 100 (e.g. <c>pu.18</c>).</item>
///   <item><c>hy.{NN}</c>  - hypnotic x 100 (e.g. <c>hy.02</c>).</item>
///   <item><c>dk.{NN}</c>  - dark x 100 (e.g. <c>dk.55</c>).</item>
///   <item><c>hx.{NN}</c>  - harshness x 100 (e.g. <c>hx.41</c>).</item>
/// </list>
/// </para>
///
/// <para>
/// All fields after <c>JP</c> are optional and omitted when the underlying value is null.
/// The fields are always emitted in the order listed above.
/// </para>
///
/// <para>
/// <b>Full example:</b><br/>
/// <c>JP|E7|K8A|bpm140|gc.57|gr.14|pu.18|hy.02|dk.55|hx.41</c>
/// </para>
///
/// <para>
/// <b>Comment field usage:</b><br/>
/// When written to the Comment tag, the JP tag is prepended to any existing user text
/// with a <c> | </c> separator (e.g. <c>JP|E7|K8A|bpm140|... | my crate note</c>).
/// <see cref="BuildComment"/> handles the idempotent prepend; <see cref="StripJpPrefix"/>
/// strips it. The existing MIK-style <c>8A - Energy 7</c> comment prefix (written by the
/// in-app DJ-comment feature) is preserved if present - it starts with a digit, not "JP".
/// </para>
///
/// <para>
/// <b>Parsing instructions for external tools (e.g. a Mac Python/bash script):</b>
/// <code>
/// # Python example
/// def parse_jp_vibe(tag: str) -> dict:
///     tag = tag.split(' | ')[0]   # strip user text
///     parts = tag.split('|')
///     if not parts or parts[0] != 'JP': return {}
///     result = {}
///     for tok in parts[1:]:
///         if tok.startswith('E')  and tok[1:].isdigit(): result['energy']       = int(tok[1:])
///         elif tok.startswith('K'):                       result['camelot']       = tok[1:]
///         elif tok.startswith('bpm'):                     result['bpm']           = int(tok[3:])
///         elif tok.startswith('gc.'): result['gridConfidence'] = int(tok[3:]) / 100.0
///         elif tok.startswith('gr.'): result['bassGroove']     = int(tok[3:]) / 100.0
///         elif tok.startswith('pu.'): result['bassPunch']      = int(tok[3:]) / 100.0
///         elif tok.startswith('hy.'): result['hypnotic']       = int(tok[3:]) / 100.0
///         elif tok.startswith('dk.'): result['dark']           = int(tok[3:]) / 100.0
///         elif tok.startswith('hx.'): result['harshness']      = int(tok[3:]) / 100.0
///     return result
/// </code>
/// </para>
/// </summary>
public static class VibeTagEncoder
{
    /// <summary>The required namespace prefix that identifies a JP vibe tag.</summary>
    public const string Prefix = "JP";

    // -- Encode -----------------------------------------------------------------

    /// <summary>
    /// Encode analysis values into a JP vibe tag string.
    /// Null parameters are omitted from the output.
    /// </summary>
    public static string Encode(
        int?    energy         = null,
        string? camelot        = null,
        double? bpm            = null,
        double? gridConfidence = null,
        double? bassGroove     = null,
        double? bassPunch      = null,
        double? hypnotic       = null,
        double? dark           = null,
        double? harshness      = null)
    {
        var sb = new StringBuilder();
        sb.Append(Prefix);

        if (energy is { } en)
            sb.Append('|').Append('E').Append(en.ToString(CultureInfo.InvariantCulture));
        if (camelot is { } k)
            sb.Append('|').Append('K').Append(k);
        if (bpm is { } b)
            sb.Append('|').Append("bpm").Append(((int)Math.Round(b)).ToString(CultureInfo.InvariantCulture));
        if (gridConfidence is { } gc)
            sb.Append('|').Append("gc.").Append(ToPct(gc).ToString("D2", CultureInfo.InvariantCulture));
        if (bassGroove is { } gr)
            sb.Append('|').Append("gr.").Append(ToPct(gr).ToString("D2", CultureInfo.InvariantCulture));
        if (bassPunch is { } pu)
            sb.Append('|').Append("pu.").Append(ToPct(pu).ToString("D2", CultureInfo.InvariantCulture));
        if (hypnotic is { } hy)
            sb.Append('|').Append("hy.").Append(ToPct(hy).ToString("D2", CultureInfo.InvariantCulture));
        if (dark is { } dk)
            sb.Append('|').Append("dk.").Append(ToPct(dk).ToString("D2", CultureInfo.InvariantCulture));
        if (harshness is { } hx)
            sb.Append('|').Append("hx.").Append(ToPct(hx).ToString("D2", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    // -- Comment helpers --------------------------------------------------------

    /// <summary>
    /// Prepend a JP vibe tag to an existing comment string (idempotent).
    /// Any existing JP| prefix is stripped first so re-writing never duplicates the tag.
    /// User text (after the JP block) is preserved, joined as <c>"{vibeTag} | {userText}"</c>.
    /// </summary>
    public static string BuildComment(string vibeTag, string? existingComment)
    {
        var userText = StripJpPrefix(existingComment);
        return string.IsNullOrEmpty(userText) ? vibeTag : $"{vibeTag} | {userText}";
    }

    /// <summary>
    /// Strip any JP| block from the front of a comment string and return the remainder
    /// (the user's own text).  Returns the input unchanged if no JP prefix is present;
    /// returns empty string if the whole comment was a JP tag.
    /// </summary>
    public static string StripJpPrefix(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "";
        if (comment != Prefix &&
            !comment.StartsWith(Prefix + "|", StringComparison.Ordinal))
            return comment;

        // Strip the JP block: find the first user-text separator " | "
        var sep = comment.IndexOf(" | ", StringComparison.Ordinal);
        return sep >= 0 ? comment[(sep + 3)..] : "";
    }

    // -- Decode ----------------------------------------------------------------

    /// <summary>Parsed fields from a JP vibe tag string.</summary>
    public sealed record VibeFields(
        int?    Energy         = null,
        string? Camelot        = null,
        double? Bpm            = null,
        double? GridConfidence = null,
        double? BassGroove     = null,
        double? BassPunch      = null,
        double? Hypnotic       = null,
        double? Dark           = null,
        double? Harshness      = null);

    /// <summary>
    /// Parse a JP vibe tag string into its component fields.
    /// Returns null if the string is not a valid JP tag.
    /// </summary>
    public static VibeFields? Decode(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        // Strip any user text after the " | " separator first.
        var sepIdx = tag.IndexOf(" | ", StringComparison.Ordinal);
        if (sepIdx >= 0) tag = tag[..sepIdx];

        var parts = tag.Split('|');
        if (parts.Length == 0 || parts[0] != Prefix) return null;

        int?    energy = null;
        string? camelot = null;
        double? bpm = null;
        double? gc  = null;
        double? gr  = null;
        double? pu  = null;
        double? hy  = null;
        double? dk  = null;
        double? hx  = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var tok = parts[i];
            if (tok.Length >= 2 && tok[0] == 'E'
                && int.TryParse(tok.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var e))
            {
                energy = e;
            }
            else if (tok.Length >= 2 && tok[0] == 'K')
            {
                camelot = tok[1..];
            }
            else if (tok.Length >= 4 && tok.StartsWith("bpm", StringComparison.Ordinal)
                && double.TryParse(tok.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            {
                bpm = b;
            }
            else if (tok.Length >= 4 && tok.StartsWith("gc.", StringComparison.Ordinal)
                && int.TryParse(tok.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                gc = n / 100.0;
            }
            else if (tok.Length >= 4 && tok.StartsWith("gr.", StringComparison.Ordinal)
                && int.TryParse(tok.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                gr = n / 100.0;
            }
            else if (tok.Length >= 4 && tok.StartsWith("pu.", StringComparison.Ordinal)
                && int.TryParse(tok.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                pu = n / 100.0;
            }
            else if (tok.Length >= 4 && tok.StartsWith("hy.", StringComparison.Ordinal)
                && int.TryParse(tok.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                hy = n / 100.0;
            }
            else if (tok.Length >= 4 && tok.StartsWith("dk.", StringComparison.Ordinal)
                && int.TryParse(tok.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                dk = n / 100.0;
            }
            else if (tok.Length >= 4 && tok.StartsWith("hx.", StringComparison.Ordinal)
                && int.TryParse(tok.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                hx = n / 100.0;
            }
        }

        return new VibeFields(energy, camelot, bpm, gc, gr, pu, hy, dk, hx);
    }

    // -- Private helpers --------------------------------------------------------

    /// <summary>Convert a [0,1] double to a 0-100 integer percentage (clamped, rounded).</summary>
    private static int ToPct(double v)
        => (int)Math.Round(Math.Clamp(v, 0.0, 1.0) * 100.0);
}
