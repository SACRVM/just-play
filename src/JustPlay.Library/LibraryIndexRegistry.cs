using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustPlay.Library;

/// <summary>
/// Which folders on THIS MACHINE have a library index - the one question every JUST app has to be able
/// to answer the same way.
///
/// <para><b>Why it lives here and not in an app's settings.</b> The root used to be a JUST PLAY finder
/// preference (<c>finder.settings.json</c>), which was fine while JUST PLAY was the only app with an
/// index. It is not a preference: it is a fact about this machine, and the index file's own location is
/// already derived from it (<see cref="LibraryDb.DefaultPathFor"/>). Having JUST TAG read another app's
/// settings file would be a per-app workaround for a shared fact - the exact divergence the suite rule
/// forbids. So the fact moves down into the library layer and every app asks the same function.</para>
///
/// <para><b>The rule for a folder that is not under any registered root:</b> it is UNINDEXED, and the
/// caller must treat it exactly as it would with no index at all - read from disk. That is not a
/// degradation, it is the whole point of JUST TAG: it is the tool you point at a download that no index
/// has ever seen.</para>
///
/// <para>Registration happens where a root is actually INDEXED (JUST PLAY's index service, the CLI's
/// analyze command), never on a mere read - so simply browsing can not conjure an index into existence.
/// A missing, empty or corrupt registry file reads as "nothing is indexed", never as an error: this
/// answers a question about speed, and nothing here may cost anyone their files.</para>
/// </summary>
public static class LibraryIndexRegistry
{
    /// <summary>The registry sits in the same suite folder as the index files themselves.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Path.GetDirectoryName(LibraryDb.DefaultPathFor("x")) ?? ".", "roots.json");

    /// <summary>
    /// Where the registry is read and written. Defaults to <see cref="DefaultPath"/>; a TEST points it
    /// at a temp file so a test run can never register anything into the real machine's registry. It is
    /// a settable static rather than an injected service on purpose: this answers a question about the
    /// MACHINE, so threading a dependency for it through three apps would be ceremony around a fact.
    /// </summary>
    public static string Location { get; set; } = DefaultPath;

    private static readonly Lock Gate = new();

    /// <summary>Every registered root, newest registration last. Empty when nothing is indexed.</summary>
    public static IReadOnlyList<string> Roots()
    {
        lock (Gate) return Load();
    }

    /// <summary>
    /// Record that <paramref name="root"/> is indexed on this machine. Idempotent, case-insensitive, and
    /// silent on failure - a read-only profile must not turn "I scanned my library" into an error.
    /// </summary>
    public static void Register(string? root)
    {
        if (Normalize(root) is not { } normalized) return;

        lock (Gate)
        {
            var roots = Load();
            if (roots.Any(r => PathsEqual(r, normalized))) return;
            Save([.. roots, normalized]);
        }
    }

    /// <summary>Forget a root (it is no longer indexed, or its index was deleted).</summary>
    public static void Unregister(string? root)
    {
        if (Normalize(root) is not { } normalized) return;

        lock (Gate)
        {
            var roots = Load();
            var kept = roots.Where(r => !PathsEqual(r, normalized)).ToList();
            if (kept.Count != roots.Count) Save(kept);
        }
    }

    /// <summary>
    /// The registered root that contains <paramref name="folder"/>, or null when none does. The LONGEST
    /// match wins, so a nested root (...\music and ...\music\GENRES) resolves to the more specific index
    /// rather than to whichever happened to be registered first.
    /// </summary>
    public static string? RootFor(string? folder) => RootFor(Roots(), folder);

    /// <summary>The same match against a GIVEN set of roots - the pure half, so the containment rules
    /// (separator boundary, longest match) are testable without a machine-global file.</summary>
    public static string? RootFor(IEnumerable<string> roots, string? folder)
    {
        if (Normalize(folder) is not { } target) return null;

        return roots
            .Select(Normalize)
            .OfType<string>()
            .Where(r => Contains(r, target))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// The index covering <paramref name="folder"/>, or null when the folder is outside every registered
    /// root or that root's database file does not exist (yet).
    ///
    /// <para>(!) It NEVER creates one. <see cref="LibraryDb.Open"/> is <c>ReadWriteCreate</c>, so calling it
    /// for an unindexed root would leave an empty database behind that then reads as "indexed" forever.
    /// The caller owns the returned instance and must dispose it.</para>
    /// </summary>
    public static LibraryDb? OpenFor(string? folder)
    {
        if (RootFor(folder) is not { } root) return null;

        var path = LibraryDb.DefaultPathFor(root);
        if (!File.Exists(path)) return null;

        try
        {
            return LibraryDb.Open(path);
        }
        catch (Exception)
        {
            // Busy, locked, or on a share that just went away - the caller falls back to reading files,
            // which is always correct, only slower.
            return null;
        }
    }

    // -- Storage ---------------------------------------------------------------------------------

    private static List<string> Load()
    {
        try
        {
            if (!File.Exists(Location)) return [];
            var json = File.ReadAllText(Location);
            var doc = JsonSerializer.Deserialize(json, LibraryRegistryJsonContext.Default.LibraryRegistryFile);
            return [.. (doc?.Roots ?? []).Select(Normalize).OfType<string>()];
        }
        catch (Exception)
        {
            return [];   // unreadable / half-written / hand-edited -> "nothing is indexed"
        }
    }

    private static void Save(List<string> roots)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
            var json = JsonSerializer.Serialize(new LibraryRegistryFile { Roots = [.. roots] },
                                                LibraryRegistryJsonContext.Default.LibraryRegistryFile);
            File.WriteAllText(Location, json);
        }
        catch (Exception)
        {
            // Best effort. The consequence of a failed write is that another app browses from disk.
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is <paramref name="folder"/> the root itself or below it? Compares on a separator
    /// boundary, so <c>...\music2</c> is not treated as being inside <c>...\music</c>.</summary>
    private static bool Contains(string root, string folder) =>
        PathsEqual(root, folder) ||
        folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        folder.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The on-disk shape of <c>roots.json</c>.</summary>
public sealed class LibraryRegistryFile
{
    [JsonPropertyName("roots")] public string[] Roots { get; set; } = [];
}

/// <summary>Source-generated JSON - trim/AOT-safe, per the repo's reflection-free goal.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LibraryRegistryFile))]
internal sealed partial class LibraryRegistryJsonContext : JsonSerializerContext;
