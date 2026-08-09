using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using JustPlay.Core.Storage;

namespace JustPlay.Core.Organise;

/// <summary>
/// The folders files were last moved or copied into, most recent first.
///
/// <para>Organising is repetitive: a folder that just landed gets split across three or four homes,
/// and each one is five clicks deep in a picker. Remembering the last few turns that into one click,
/// and it is the cheapest possible feature - a list of strings in a small file.</para>
///
/// <para>Suite-wide rather than per app (it lives beside the library index, not in JUST TAG's own
/// settings): the destination is a fact about where her music goes, and a second app that grew the
/// same menu would otherwise start from an empty list.</para>
///
/// <para>Missing, empty or corrupt reads as "no recents" and a failed write is silent - the
/// consequence is one extra trip through the picker, which is not worth an error.</para>
/// </summary>
public static class RecentDestinations
{
    /// <summary>How many to keep. Long enough to cover an evening's genres, short enough that the
    /// list is still something you scan rather than read.</summary>
    public const int Keep = 8;

    /// <summary>
    /// Where the list is read and written. Settable so a test never touches the real machine's
    /// file - the same seam <c>LibraryIndexRegistry.Location</c> uses, for the same reason.
    /// </summary>
    public static string Location { get; set; } =
        JustDataPaths.Combine("JUST", "recent-destinations.json");

    private static readonly Lock Gate = new();

    /// <summary>The remembered folders, most recent first. Ones that are no longer there are left
    /// out - offering a dead share as a destination is offering a dead end.</summary>
    public static IReadOnlyList<string> All()
    {
        lock (Gate) return [.. Load().Where(Directory.Exists)];
    }

    /// <summary>Put a folder at the top of the list. Idempotent and case-insensitive.</summary>
    public static void Remember(string? folder)
    {
        if (OrganisePlanner.Normalise(folder) is not { } normalized) return;

        lock (Gate)
        {
            var kept = new List<string> { normalized };
            kept.AddRange(Load()
                .Where(f => !string.Equals(f, normalized, StringComparison.OrdinalIgnoreCase))
                .Take(Keep - 1));
            Save(kept);
        }
    }

    /// <summary>Forget everything. Exists for the tests and for a future "clear" in settings.</summary>
    public static void Clear()
    {
        lock (Gate) Save([]);
    }

    // -- Storage ---------------------------------------------------------------------------------

    private static List<string> Load()
    {
        try
        {
            if (!File.Exists(Location)) return [];
            var json = File.ReadAllText(Location);
            var doc = JsonSerializer.Deserialize(
                json, RecentDestinationsJsonContext.Default.RecentDestinationsFile);
            return [.. (doc?.Folders ?? []).Select(OrganisePlanner.Normalise).OfType<string>()];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Save(List<string> folders)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
            var json = JsonSerializer.Serialize(
                new RecentDestinationsFile { Folders = [.. folders] },
                RecentDestinationsJsonContext.Default.RecentDestinationsFile);
            File.WriteAllText(Location, json);
        }
        catch (Exception)
        {
            // Best effort - see the class remarks.
        }
    }
}

/// <summary>The on-disk shape.</summary>
public sealed class RecentDestinationsFile
{
    [JsonPropertyName("folders")] public string[] Folders { get; set; } = [];
}

/// <summary>Source-generated JSON - trim/AOT-safe, per the repo's reflection-free goal.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RecentDestinationsFile))]
internal sealed partial class RecentDestinationsJsonContext : JsonSerializerContext;
