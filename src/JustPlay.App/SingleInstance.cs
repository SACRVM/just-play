using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using JustPlay.Core.Storage;

namespace JustPlay.App;

/// <summary>
/// Single-instance gate + file-argument forwarding. The first JustPlay process owns a named
/// mutex and listens on a named pipe; any later process started by a double-click / file
/// association forwards its file paths to the owner and exits — so songs land in the existing
/// queue instead of opening a second window. Cross-platform: Mutex and named pipes both work
/// on Windows, Linux and macOS.
///
/// Names are suffixed with the user name so two users on one machine each get their own
/// instance (matches the per-user install), AND with the data directory, so an instance running
/// on a redirected <c>JUSTPLAY_DATA_DIR</c> is a different INSTALLATION and gets its own gate.
///
/// <para>⚠ Without that second part the fresh-install run is unusable: launching it while the real
/// JUST PLAY is open makes the new process forward-and-exit, and you sit there looking at your real
/// library wondering why nothing reset (Chloe 2026-07-31: "mein just play music lib ordner und index
/// wurden nicht zurück gesetzt - konnte die neue logik nicht testen"). The gate exists to keep ONE
/// instance owning ONE queue and ONE settings file — two different data roots are not that case.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Suffix = Sanitize(Environment.UserName) + DataScope();
    private static readonly string MutexName = $"JustPlay.SingleInstance.v1.{Suffix}";
    private static readonly string PipeName  = $"JustPlay.OpenFiles.v1.{Suffix}";

    /// <summary>
    /// Empty for a normal install (so the gate name is unchanged and an existing running instance is
    /// still found), a short stable hash of the data root when it has been redirected.
    /// </summary>
    private static string DataScope()
    {
        if (!JustDataPaths.IsOverridden) return "";

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JustDataPaths.Base.ToLowerInvariant()));
        return "." + Convert.ToHexStringLower(bytes)[..8];
    }

    private Mutex? _mutex;
    private volatile bool _running = true;

    /// <summary>True when this is the first (owning) instance.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>Try to become the primary instance. Returns false if one is already running.</summary>
    public bool Acquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsPrimary = createdNew;
        return createdNew;
    }

    /// <summary>Split raw argv into the existing file paths and the "append only" flag
    /// (the future "Add to JustPlay" verb passes <c>--add</c>; a bare path is "open").</summary>
    public static (IReadOnlyList<string> Files, bool AddOnly) ParseFileArgs(string[] args)
    {
        var addOnly = args.Any(a => a.Equals("--add", StringComparison.OrdinalIgnoreCase));
        var files = args
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToList();
        return (files, addOnly);
    }

    /// <summary>Hand a file list to the already-running primary instance (best effort).</summary>
    public static void ForwardToPrimary(IReadOnlyList<string> files, bool addOnly)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000); // primary should answer well within 2s
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(addOnly ? "ADD" : "OPEN");
            foreach (var f in files) w.WriteLine(f);
        }
        catch
        {
            // Primary went away between the mutex check and the connect, or is busy — best
            // effort. Worst case the file just isn't added; never crash a forwarding launch.
        }
    }

    /// <summary>Start listening (on a background thread) for files forwarded by later launches.
    /// <paramref name="onReceived"/> is invoked off the UI thread — marshal inside it.</summary>
    public void StartServer(Action<IReadOnlyList<string>, bool> onReceived)
    {
        var t = new Thread(() => ServerLoop(onReceived))
        {
            IsBackground = true,
            Name = "JustPlay-IPC",
        };
        t.Start();
    }

    private void ServerLoop(Action<IReadOnlyList<string>, bool> onReceived)
    {
        while (_running)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();

                using var r = new StreamReader(server);
                var header = r.ReadLine();
                if (header is null) continue;
                var addOnly = header.Trim().Equals("ADD", StringComparison.OrdinalIgnoreCase);

                var files = new List<string>();
                string? line;
                while ((line = r.ReadLine()) is not null)
                    if (!string.IsNullOrWhiteSpace(line)) files.Add(line);

                if (files.Count > 0) onReceived(files, addOnly);
            }
            catch
            {
                // A malformed/aborted client must not kill the server loop.
            }
        }
    }

    private static string Sanitize(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length > 0 ? new string(chars) : "user";
    }

    public void Dispose()
    {
        _running = false;
        _mutex?.Dispose();
        _mutex = null;
    }
}
