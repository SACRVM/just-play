using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MakeTestLib;

/// <summary>
/// Everything that stands between "reset the test library" and "delete the wrong folder".
///
/// <para>A reset removes a whole directory tree, so it is not allowed to be a parameter. The root is
/// a constant in this tool; a path may be passed on the command line only so it can be CHECKED, and
/// anything that is not the one allowed root is refused before a single file is touched. On top of
/// that the folder has to prove it was made here (its manifest), it may not contain a file type this
/// tool never writes, it may not contain a file far bigger than anything this tool writes, and it may
/// not contain a junction or symlink that would carry the delete outside the tree.</para>
/// </summary>
internal static class Guard
{
    /// <summary>Nothing this tool writes comes close - the biggest is a 7 s stereo WAV at 1.2 MB.
    /// A real track that wandered in here trips this and stops the reset.</summary>
    private const long MaxFileBytes = 3 * 1024 * 1024;

    private static readonly string[] AudioExtensions = [".wav", ".aiff", ".mp3", ".flac"];

    private static readonly string[] ToolFiles = [TestLib.ManifestName, TestLib.ReadmeName, TestLib.ResetName];

    public sealed class RefusedException(string message) : Exception(message);

    /// <summary>Normalise and compare against the single allowed root. Throws on anything else.</summary>
    public static string ResolveRoot(string? requested)
    {
        var allowed = Normalize(TestLib.Root);
        if (string.IsNullOrWhiteSpace(requested)) return allowed;

        string candidate;
        try { candidate = Normalize(requested); }
        catch (Exception ex)
        {
            throw new RefusedException($"Refused: '{requested}' is not a usable path ({ex.Message}).");
        }

        if (!string.Equals(candidate, allowed, StringComparison.OrdinalIgnoreCase))
            throw new RefusedException(
                $"Refused: this tool only ever touches {allowed}. It was asked to use {candidate}.");

        return allowed;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Everything that makes the folder unsafe to wipe. An empty list means: this is our tree, and
    /// nothing in it is a stranger.
    /// </summary>
    public static List<string> Inspect(string root)
    {
        var problems = new List<string>();
        if (!Directory.Exists(root)) return problems; // nothing to delete is always safe

        if (IsReparsePoint(root))
        {
            problems.Add($"{root} is a junction or symlink, not a real folder.");
            return problems;
        }

        var manifest = Path.Combine(root, TestLib.ManifestName);
        if (!File.Exists(manifest))
        {
            problems.Add($"{TestLib.ManifestName} is missing - this folder was not made by {TestLib.Tool}.");
            return problems;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var tool = doc.RootElement.TryGetProperty("tool", out var t) ? t.GetString() : null;
            var declared = doc.RootElement.TryGetProperty("root", out var r) ? r.GetString() : null;

            if (!string.Equals(tool, TestLib.Tool, StringComparison.Ordinal))
                problems.Add($"{TestLib.ManifestName} was written by '{tool}', not by {TestLib.Tool}.");

            if (!string.Equals(declared, root, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{TestLib.ManifestName} belongs to '{declared}', not to {root}.");
        }
        catch (Exception ex)
        {
            problems.Add($"{TestLib.ManifestName} could not be read ({ex.Message}).");
            return problems;
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (IsReparsePoint(dir))
                problems.Add($"Junction or symlink inside the tree: {dir}");
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var known = ToolFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                        || AudioExtensions.Contains(ext);

            if (!known)
            {
                problems.Add($"Not something {TestLib.Tool} writes: {file}");
                continue;
            }

            var length = new FileInfo(file).Length;
            if (length > MaxFileBytes)
                problems.Add($"Far bigger than anything {TestLib.Tool} writes ({length / 1024 / 1024} MB): {file}");
        }

        return problems;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>
    /// Delete the tree's contents after <see cref="Inspect"/> came back clean. RESET.ps1 itself is
    /// left in place: it is very likely the script that is running right now, and re-creating it is
    /// unnecessary anyway because its content is fixed.
    /// </summary>
    public static int Clean(string root)
    {
        if (!Directory.Exists(root)) return 0;

        var removed = 0;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            Directory.Delete(dir, recursive: true);
            removed++;
        }

        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (string.Equals(Path.GetFileName(file), TestLib.ResetName, StringComparison.OrdinalIgnoreCase))
                continue;

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
            removed++;
        }

        return removed;
    }

    /// <summary>Write the inventory the guard reads back, and a reset compares against. It carries
    /// no timestamp on purpose: the whole tree, this file included, must come back identical.</summary>
    public static void WriteManifest(string root, IEnumerable<string> relativePaths)
    {
        var entries = new List<object>();
        foreach (var rel in relativePaths.OrderBy(p => p, StringComparer.Ordinal))
        {
            var full = Path.Combine(root, rel);
            entries.Add(new
            {
                path = rel.Replace('\\', '/'),
                bytes = new FileInfo(full).Length,
                sha256 = Sha256(full),
            });
        }

        var json = JsonSerializer.Serialize(
            new { tool = TestLib.Tool, root, files = entries },
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(Path.Combine(root, TestLib.ManifestName), json, new UTF8Encoding(false));
    }

    /// <summary>True for the four containers this tool writes.</summary>
    public static bool IsAudio(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
