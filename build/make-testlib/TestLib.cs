using System.Text;

namespace MakeTestLib;

internal enum AudioFormat { Wav, Aiff, Mp3, Flac }

/// <summary>
/// One file the tree is supposed to contain. Everything about it - length, pitch, tags, timestamp -
/// is derived from <see cref="Seed"/> and nothing else, which is what makes two copies of the same
/// name byte for byte identical unless a case deliberately wants them different.
/// </summary>
internal sealed record PlannedFile
{
    /// <summary>Folder relative to the root; the empty string is the root itself.</summary>
    public required string Folder { get; init; }

    public required string Name { get; init; }

    /// <summary>Set only where a case needs two files of the SAME name to differ.</summary>
    public string? Variant { get; init; }

    public bool Cover { get; init; }

    public string Seed => Variant ?? Name;

    public string Relative => Folder.Length == 0 ? Name : Path.Combine(Folder, Name);

    public AudioFormat Format => Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".wav" => AudioFormat.Wav,
        ".aiff" => AudioFormat.Aiff,
        ".flac" => AudioFormat.Flac,
        _ => AudioFormat.Mp3,
    };
}

/// <summary>The shape of the throwaway library: which folders exist and what is in them.</summary>
internal static class TestLib
{
    public const string Root = @"D:\JUST-TESTLIB";
    public const string ManifestName = "_manifest.json";
    public const string ReadmeName = "README.txt";
    public const string ResetName = "RESET.ps1";
    public const string Tool = "make-testlib";

    /// <summary>The one duplicated name that is byte for byte identical in two folders (same
    /// bytes, same timestamp) - the "nothing changed here" side of skip-unchanged logic.</summary>
    public const string UnchangedTwin = "Bulk 06 - Falcon.mp3";

    /// <summary>The one duplicated name whose two copies deliberately differ in size AND
    /// timestamp - the "this one did change" side.</summary>
    public const string ChangedTwin = "Collider - Same Name Twice.mp3";

    public static readonly string[] Folders =
    [
        "01 BULK",
        "02 CRATES",
        @"02 CRATES\HARD TECHNO",
        @"02 CRATES\HARD TECHNO\DEEP CUTS",
        @"02 CRATES\VINAHOUSE",
        "03 TARGET HAS IT",
        "04 AWKWARD NAMES",
        @"04 AWKWARD NAMES\Bits & Bobs (2026)",
        "05 MIXED FORMATS",
        "06 EMPTY LANDING",
    ];

    private static readonly string[] BulkWords =
    [
        "Alpha", "Bravo", "Cinder", "Delta", "Ember", "Falcon", "Granite", "Harbour", "Indigo",
        "Jasper", "Kestrel", "Larkspur", "Marble", "Nimbus", "Onyx", "Petra", "Quartz", "Ripple",
        "Solstice", "Tundra", "Umber", "Vellum", "Willow", "Xenon", "Yarrow", "Zephyr", "Amber",
        "Basalt", "Cobalt", "Drift",
    ];

    private const string LongName =
        "A Very Long Name That Just Keeps Going And Going Because Some Producers " +
        "Really Do Name Their Files Like This Extended Club Mix.mp3";

    public static IReadOnlyList<PlannedFile> Files()
    {
        var files = new List<PlannedFile>();

        // 01 BULK - 30 files, so "select everything and move it" is a real bulk operation.
        // The extension pattern is a fixed rule, not a coin toss: 6 FLAC, 4 WAV, 2 AIFF, 18 MP3.
        for (var i = 1; i <= BulkWords.Length; i++)
        {
            var ext = i % 5 == 0 ? ".flac" : i % 7 == 0 ? ".wav" : i % 11 == 0 ? ".aiff" : ".mp3";
            files.Add(new PlannedFile
            {
                Folder = "01 BULK",
                Name = $"Bulk {i:00} - {BulkWords[i - 1]}{ext}",
                Cover = i == 12,
            });
        }

        // 02 CRATES - two crates that hold the SAME file name, so moving one into the other
        // collides. The two copies differ in length, hence in size, hence in timestamp.
        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO", Name = ChangedTwin, Variant = "collider-long" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO", Name = "Kickdrum Study.mp3" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO", Name = "Rumble Bench.flac" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO", Name = "Warehouse Sweep.wav" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO", Name = "Peak Time Filler.mp3" });

        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO\DEEP CUTS", Name = "Deep Cut One.mp3" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO\DEEP CUTS", Name = "Deep Cut Two.flac" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\HARD TECHNO\DEEP CUTS", Name = "Deep Cut Three.aiff", Cover = true });

        files.Add(new PlannedFile { Folder = @"02 CRATES\VINAHOUSE", Name = ChangedTwin, Variant = "collider-short" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\VINAHOUSE", Name = "Bounce Bench.mp3" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\VINAHOUSE", Name = "Vina Snare Test.flac" });
        files.Add(new PlannedFile { Folder = @"02 CRATES\VINAHOUSE", Name = "Slow Ride Study.wav" });

        // 03 TARGET HAS IT - a destination that ALREADY holds a name coming out of 01 BULK, and
        // holds it as the identical file, so a copy or move into here is the unchanged case.
        files.Add(new PlannedFile { Folder = "03 TARGET HAS IT", Name = UnchangedTwin });
        files.Add(new PlannedFile { Folder = "03 TARGET HAS IT", Name = "Landing Pad One.mp3" });
        files.Add(new PlannedFile { Folder = "03 TARGET HAS IT", Name = "Landing Pad Two.flac" });

        // THE ROOT ITSELF - four files, one per format, and they are here for a reason that is not
        // about organising at all: a folder holding only SUBFOLDERS gives an empty file pane, and an
        // empty pane with nothing to look at reads as an app that failed to load. The first screen of
        // a test library must not be the one that makes you doubt the build. (The app should say why
        // a pane is empty too - that is a separate fix - but a sandbox whose front door is blank is a
        // bad sandbox regardless.)
        files.Add(new PlannedFile { Folder = "", Name = "Start Here - One.mp3" });
        files.Add(new PlannedFile { Folder = "", Name = "Start Here - Two.flac" });
        files.Add(new PlannedFile { Folder = "", Name = "Start Here - Three.wav" });
        files.Add(new PlannedFile { Folder = "", Name = "Start Here - Four.aiff" });

        // 04 AWKWARD NAMES - legal, but the kind of name that breaks naive path handling.
        files.Add(new PlannedFile { Folder = "04 AWKWARD NAMES", Name = "01 leading number.wav" });
        files.Add(new PlannedFile { Folder = "04 AWKWARD NAMES", Name = "spaces   and   more   spaces.mp3" });
        files.Add(new PlannedFile { Folder = "04 AWKWARD NAMES", Name = "brackets [live] (2026) mix.mp3" });
        files.Add(new PlannedFile { Folder = "04 AWKWARD NAMES", Name = "rock & roll & more.flac" });
        files.Add(new PlannedFile { Folder = "04 AWKWARD NAMES", Name = "dot.in.the.middle.name.wav" });
        files.Add(new PlannedFile { Folder = "04 AWKWARD NAMES", Name = LongName });
        files.Add(new PlannedFile { Folder = "04 AWKWARD NAMES", Name = "UPPER lower MiXeD.aiff" });

        files.Add(new PlannedFile { Folder = @"04 AWKWARD NAMES\Bits & Bobs (2026)", Name = "inside 01.wav" });
        files.Add(new PlannedFile { Folder = @"04 AWKWARD NAMES\Bits & Bobs (2026)", Name = "inside & out.mp3" });

        // 05 MIXED FORMATS - one of every format the generator can make, side by side.
        files.Add(new PlannedFile { Folder = "05 MIXED FORMATS", Name = "One Of Each - Wave.wav" });
        files.Add(new PlannedFile { Folder = "05 MIXED FORMATS", Name = "One Of Each - Aiff.aiff" });
        files.Add(new PlannedFile { Folder = "05 MIXED FORMATS", Name = "One Of Each - Mp3.mp3", Cover = true });
        files.Add(new PlannedFile { Folder = "05 MIXED FORMATS", Name = "One Of Each - Flac.flac", Cover = true });

        // 06 EMPTY LANDING stays empty on purpose - a clean destination with nothing to collide with.

        return files;
    }

    // -- Derived, deterministic per file values -----------------------------------------------

    private static readonly string[] Artists =
    [
        "Test Signal", "Sine Sisters", "The Nulls", "Placeholder Crew",
        "Bench Unit", "Dummy Load", "Grey Noise Co", "Null Pointer",
    ];

    private static readonly string[] Albums =
    [
        "Test Library Vol 1", "Test Library Vol 2", "Test Library Vol 3", "Bench Sessions",
    ];

    private static readonly string[] Genres =
    [
        "Hard Techno", "Vinahouse", "Test Tones", "Hardstyle", "House",
    ];

    /// <summary>FNV-1a over the seed. Spelled out rather than taken from a library so the value
    /// cannot change with a runtime update - a reset has to reproduce the same tree next year.</summary>
    public static uint Hash(string seed)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(seed))
        {
            h ^= b;
            h *= 16777619u;
        }
        return h;
    }

    public static string Artist(uint seed) => Artists[(int)(seed % (uint)Artists.Length)];
    public static string Album(uint seed) => Albums[(int)(seed / 3 % (uint)Albums.Length)];
    public static string Genre(uint seed) => Genres[(int)(seed / 11 % (uint)Genres.Length)];
    public static uint Year(uint seed) => 2019 + seed % 8;
    public static uint Track(uint seed) => seed / 5 % 20 + 1;

    /// <summary>Title = the file name without its extension, which is what a tagger would show
    /// anyway, and it keeps the awkward names visible in the editor.</summary>
    public static string Title(PlannedFile file) => Path.GetFileNameWithoutExtension(file.Name);

    /// <summary>A fixed timestamp per seed, so a reset restores the modification times too.</summary>
    public static DateTime Modified(uint seed) =>
        new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc).AddMinutes(seed % 14400);

    public const string CommentLine =
        "JUST-TESTLIB synthetic test file. Safe to move, copy and delete.";
}
