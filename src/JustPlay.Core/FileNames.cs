namespace JustPlay.Core;

/// <summary>
/// Cross-platform file-name sanitization. Uses the WINDOWS invalid-character set on every OS:
/// <c>Path.GetInvalidFileNameChars()</c> returns only '/' and NUL on Unix, but everything we
/// write (recordings, playlist bundles) lands on the SMB library share that Windows, macOS and
/// Traktor all read — so the strictest set must apply everywhere, deterministically.
/// </summary>
public static class FileNames
{
    // Windows-invalid printable chars; control chars (0x00–0x1F) are handled in Sanitize.
    private static readonly char[] Invalid = ['"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    /// <summary>Replace every invalid character with '_' (control chars included).</summary>
    public static string Sanitize(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(c < ' ' || Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
