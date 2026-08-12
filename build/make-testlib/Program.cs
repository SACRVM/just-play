using System.Text;
using JustPlay.Core.Models;
using JustPlay.Metadata;
using MakeTestLib;

// Generates D:\JUST-TESTLIB - a throwaway library of SYNTHESISED, tagged, playable audio files for
// hand testing JUST TAG's ORGANISE feature (move / copy / delete) without risking a real library.
// Nothing is copied from anywhere: every sample is computed here, and every file is written here.
//
//   (no switch)   generate; refuses if the folder already exists
//   --reset       wipe and regenerate, after the safety checks pass
//   --check       run the safety checks and report; changes nothing
//   --verify      read every file back with the app's own metadata reader
//   --root PATH   the folder to act on - accepted only when it IS D:\JUST-TESTLIB, so that
//                 pointing the reset somewhere else is a refusal and not an accident

var reset = args.Contains("--reset", StringComparer.Ordinal);
var check = args.Contains("--check", StringComparer.Ordinal);
var verify = args.Contains("--verify", StringComparer.Ordinal);
var rootArg = ArgValue("--root");

string root;
try
{
    root = Guard.ResolveRoot(rootArg);
}
catch (Guard.RefusedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

if (check) return Check(root);
if (verify) return Verify(root);

if (Directory.Exists(root) && !reset)
{
    Console.Error.WriteLine($"{root} already exists. Use the reset switch to wipe and regenerate it.");
    return 1;
}

if (reset)
{
    var problems = Guard.Inspect(root);
    if (problems.Count > 0)
    {
        Console.Error.WriteLine($"Refused to reset {root}:");
        foreach (var p in problems) Console.Error.WriteLine("  - " + p);
        Console.Error.WriteLine("Nothing was deleted. Look at what is in there before running this again.");
        return 1;
    }

    var removed = Guard.Clean(root);
    Console.WriteLine($"Cleared {root} ({removed} top-level entries).");
}

Generate(root);
return 0;

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Check(string root)
{
    if (!Directory.Exists(root))
    {
        Console.WriteLine($"{root} does not exist.");
        return 0;
    }

    var problems = Guard.Inspect(root);
    if (problems.Count == 0)
    {
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
        Console.WriteLine($"{root} is safe to reset ({files} files, nothing unexpected in it).");
        return 0;
    }

    Console.Error.WriteLine($"{root} would NOT be reset:");
    foreach (var p in problems) Console.Error.WriteLine("  - " + p);
    return 1;
}

static int Verify(string root)
{
    var reader = new TagLibMetadataReader();
    var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => Guard.IsAudio(f))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();

    var bad = 0;
    foreach (var file in files)
    {
        var meta = reader.Read(file);
        var ok = meta.Duration > TimeSpan.Zero
                 && !string.IsNullOrEmpty(meta.Artist)
                 && !string.IsNullOrEmpty(meta.Title);
        if (!ok) bad++;

        Console.WriteLine(string.Format(
            "{0} {1,-5} {2,6:0.00}s {3} {4,-16} {5,-20} {6}",
            ok ? "ok  " : "FAIL",
            Path.GetExtension(file).TrimStart('.').ToUpperInvariant(),
            meta.Duration.TotalSeconds,
            meta.CoverArt is { Length: > 0 } ? "cover" : "     ",
            meta.Artist ?? "",
            meta.Album ?? "",
            Path.GetRelativePath(root, file)));
    }

    Console.WriteLine($"{files.Count} files read back, {bad} unreadable.");
    return bad == 0 ? 0 : 1;
}

static void Generate(string root)
{
    Directory.CreateDirectory(root);
    foreach (var folder in TestLib.Folders)
        Directory.CreateDirectory(Path.Combine(root, folder));

    var writer = new TagLibMetadataWriter();
    using var encoder = new BassEncoder();
    encoder.Open();

    var planned = TestLib.Files();
    var written = new List<string>();
    var perFolder = new Dictionary<string, int>(StringComparer.Ordinal);
    var perFormat = new Dictionary<AudioFormat, int>();

    foreach (var file in planned)
    {
        var full = Path.Combine(root, file.Relative);
        var seed = TestLib.Hash(file.Seed);
        var samples = Pcm.Render(seed);

        switch (file.Format)
        {
            case AudioFormat.Wav: Pcm.WriteWav(full, samples); break;
            case AudioFormat.Aiff: Pcm.WriteAiff(full, samples); break;
            case AudioFormat.Mp3: encoder.WriteMp3(full, samples); break;
            case AudioFormat.Flac: encoder.WriteFlac(full, samples); break;
        }

        var tags = new EditableTags
        {
            Title = TestLib.Title(file),
            Artist = TestLib.Artist(seed),
            Album = TestLib.Album(seed),
            AlbumArtist = TestLib.Artist(seed),
            Genre = TestLib.Genre(seed),
            Year = TestLib.Year(seed),
            TrackNumber = TestLib.Track(seed),
            Comment = TestLib.CommentLine,
        };

        writer.WriteEditable(full, tags,
            file.Cover ? CoverAction.Replace : CoverAction.Keep,
            file.Cover ? CoverPng.Render(seed) : null,
            "image/png");

        File.SetLastWriteTimeUtc(full, TestLib.Modified(seed));

        written.Add(file.Relative);
        perFolder[file.Folder] = perFolder.GetValueOrDefault(file.Folder) + 1;
        perFormat[file.Format] = perFormat.GetValueOrDefault(file.Format) + 1;
    }

    // The two documents, then the inventory that describes all three plus every audio file.
    var projectDir = FindProjectDir();
    WriteIfChanged(Path.Combine(root, TestLib.ReadmeName), Docs.Readme(projectDir));
    WriteIfChanged(Path.Combine(root, TestLib.ResetName), Docs.ResetScript(projectDir));
    written.Add(TestLib.ReadmeName);
    written.Add(TestLib.ResetName);

    Guard.WriteManifest(root, written);

    foreach (var name in (string[])[TestLib.ReadmeName, TestLib.ResetName, TestLib.ManifestName])
        File.SetLastWriteTimeUtc(Path.Combine(root, name), TestLib.Modified(TestLib.Hash(name)));

    // -- Report ----------------------------------------------------------------------------
    long bytes = 0;
    foreach (var rel in written) bytes += new FileInfo(Path.Combine(root, rel)).Length;

    Console.WriteLine();
    Console.WriteLine($"{root}");
    foreach (var folder in TestLib.Folders)
        Console.WriteLine($"  {folder,-38} {perFolder.GetValueOrDefault(folder),3} files");

    Console.WriteLine();
    Console.WriteLine("  formats: " + string.Join(", ",
        perFormat.OrderBy(p => p.Key).Select(p => $"{p.Value} {p.Key.ToString().ToUpperInvariant()}")));
    Console.WriteLine($"  {planned.Count} audio files, {bytes / 1024} KB in total.");
    Console.WriteLine($"  README.txt, RESET.ps1 and {TestLib.ManifestName} written.");
}

static void WriteIfChanged(string path, string content)
{
    // RESET.ps1 is very likely the script running this right now, so it is never rewritten
    // unless it actually differs. Its content is fixed, so on a normal reset it does not.
    var normalised = content.ReplaceLineEndings("\r\n");
    if (File.Exists(path) && File.ReadAllText(path) == normalised) return;
    File.WriteAllText(path, normalised, new UTF8Encoding(false));
}

static string FindProjectDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "make-testlib.csproj"))) return dir.FullName;
        dir = dir.Parent;
    }
    return @"D:\repos\just-play\build\make-testlib";
}
