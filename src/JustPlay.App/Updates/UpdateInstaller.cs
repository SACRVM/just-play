using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using JustPlay.Core.Storage;
using JustPlay.Core.Updates;

namespace JustPlay.App.Updates;

/// <summary>
/// Downloads a release's installer and hands off to it so JustPlay can update itself while
/// running. The hand-off works around Windows file-locking: a detached PowerShell helper waits
/// for this process to exit, runs the Inno installer silently (an over-install into the same
/// per-user directory — no UAC, since the installer is <c>PrivilegesRequired=lowest</c>), then
/// relaunches JustPlay. Self-update only applies to installer builds; a portable copy (no
/// <c>unins000.exe</c> alongside the exe) falls back to the browser.
/// <para>
/// This is the App-layer counterpart to <see cref="GitHubUpdateChecker"/> (Core): the checker
/// only reports what's available; the download + process hand-off (OS specifics) live here, the
/// same layering split as the audio / metadata adapters.
/// </para>
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>
    /// True if this build was installed by the Inno setup (Inno drops <c>unins000.exe</c> next to
    /// the exe). <paramref name="installDir"/> is the directory to update + relaunch from.
    /// </summary>
    public static bool TryGetInnoInstallDir(out string installDir)
    {
        installDir = string.Empty;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;
        var dir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(dir)) return false;
        if (!File.Exists(Path.Combine(dir, "unins000.exe"))) return false;
        installDir = dir;
        return true;
    }

    /// <summary>
    /// Download the installer asset into <c>%LOCALAPPDATA%\JustPlay\updates</c>. Verifies the byte
    /// count against the release metadata when known.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.InstallerAssetUrl))
        {
            throw new InvalidOperationException("Release has no installer asset to download.");
        }

        var dir = JustDataPaths.Combine("JustPlay", "updates");
        Directory.CreateDirectory(dir);

        var name = string.IsNullOrEmpty(info.InstallerAssetName)
            ? $"JustPlaySetup-{info.Version.ToString(3)}-win-x64.exe"
            : info.InstallerAssetName;
        var dest = Path.Combine(dir, name);

        using (var resp = await http.GetAsync(info.InstallerAssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = File.Create(dest);
            await src.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        if (info.InstallerAssetSize > 0)
        {
            var len = new FileInfo(dest).Length;
            if (len != info.InstallerAssetSize)
            {
                throw new IOException($"Downloaded installer is {len} bytes, expected {info.InstallerAssetSize}.");
            }
        }

        return dest;
    }

    /// <summary>
    /// Spawn the detached helper that performs the swap, then shut JustPlay down so its files
    /// unlock. The helper relaunches JustPlay when the installer finishes. Must be called on the UI
    /// thread (it shuts the desktop lifetime down).
    /// </summary>
    public static void LaunchAndExit(string installerPath, string installDir)
    {
        // Installed exe name comes from installer/justplay.iss (#define AppExe). Fall back to any
        // JustPlay*.exe (never the CLI or the uninstaller) so a FUTURE exe rename can't strand the
        // relaunch — that is exactly what broke 0.3.0 -> 0.3.1: the old build relaunched a
        // since-renamed exe and the orphaned old exe kept coming back up.
        var exe = Path.Combine(installDir, "JustPlay.exe");
        if (!File.Exists(exe))
        {
            exe = Directory.EnumerateFiles(installDir, "JustPlay*.exe")
                .FirstOrDefault(p =>
                {
                    var n = Path.GetFileName(p);
                    return !n.Equals("JustPlayCLI.exe", StringComparison.OrdinalIgnoreCase)
                        && !n.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
                }) ?? exe;
        }
        var pid = Environment.ProcessId;

        // PowerShell over a .bat: Wait-Process is a clean "wait for our exit" primitive and
        // -EncodedCommand sidesteps all path-quoting pitfalls.
        var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
try { Wait-Process -Id {{pid}} -Timeout 90 } catch { }
Start-Process -FilePath '{{Esc(installerPath)}}' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS' -Wait
Start-Process -FilePath '{{Esc(exe)}}'
""";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        // Shutting down runs the normal close path (settings persisted, engine disposed) so the
        // exe + its locked native DLLs are released before the installer over-writes them.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }

    // Single-quote escaping for PowerShell single-quoted string literals.
    private static string Esc(string s) => s.Replace("'", "''");
}
