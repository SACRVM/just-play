using System.Text.Json;
using JustPlay.Cli.Reports;

namespace JustPlay.Cli.Commands;

/// <summary>
/// <c>justplay scan &lt;root&gt; [--json &lt;out&gt;]</c>
///
/// Recursively inventories audio files under <paramref name="root"/>.
/// Reports: total count + size, per-format counts, per-folder counts.
/// READ-ONLY — never modifies any file.
/// </summary>
internal static class ScanCommand
{
    public static int Run(string root, string? jsonOut)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[scan] ERROR: directory not found: {root}");
            return 1;
        }

        Console.WriteLine($"[scan] Scanning: {root}");

        var byFormat = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        int totalFiles  = 0;

        foreach (var file in AudioFiles.Enumerate(root))
        {
            var ext    = Path.GetExtension(file).ToLowerInvariant();
            var folder = Path.GetDirectoryName(file) ?? root;
            var size   = new FileInfo(file).Length;

            totalFiles++;
            totalBytes += size;
            byFormat[ext]    = byFormat.GetValueOrDefault(ext)    + 1;
            byFolder[folder] = byFolder.GetValueOrDefault(folder) + 1;
        }

        var report = new ScanReport
        {
            Root        = root,
            TotalFiles  = totalFiles,
            TotalBytes  = totalBytes,
            TotalSizeGb = totalBytes / (1024.0 * 1024.0 * 1024.0),
            ByFormat    = byFormat,
            ByFolder    = byFolder,
        };

        // ── Console summary ──────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"  Total files : {report.TotalFiles:N0}");
        Console.WriteLine($"  Total size  : {AudioFiles.FormatBytes(report.TotalBytes)}");
        Console.WriteLine();
        Console.WriteLine("  By format:");
        foreach (var (ext, count) in report.ByFormat.OrderByDescending(x => x.Value))
            Console.WriteLine($"    {ext,-8} {count,6:N0}");
        Console.WriteLine();
        Console.WriteLine($"  Unique folders : {report.ByFolder.Count:N0}");

        // Print the top 20 folders by count
        var topFolders = report.ByFolder
            .OrderByDescending(x => x.Value)
            .Take(20);
        foreach (var (folder, count) in topFolders)
            Console.WriteLine($"    [{count,4}] {folder}");
        if (report.ByFolder.Count > 20)
            Console.WriteLine($"    … and {report.ByFolder.Count - 20} more folders.");

        // ── JSON output ──────────────────────────────────────────────────────
        if (jsonOut is not null)
        {
            var json = JsonSerializer.Serialize(report, CliJsonContext.Default.ScanReport);
            var dir  = Path.GetDirectoryName(jsonOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(jsonOut, json);
            Console.WriteLine();
            Console.WriteLine($"  JSON written to: {jsonOut}");
        }

        return 0;
    }
}
