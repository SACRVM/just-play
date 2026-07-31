using System;
using System.IO;
using JustPlay.Core.Storage;

namespace JustPlay.Core.Logging;

/// <summary>
/// Persists an app's event log to a rolling daily file in <c>%LOCALAPPDATA%\&lt;appFolder&gt;</c>
/// (<c>session-YYYY-MM-DD.log</c>) so a DJ can review a session afterwards — when a connection dropped,
/// what was recorded, which file couldn't be tagged. This is the SAME feed shown live in the shared log
/// window, not low-level BASS console diagnostics (those live in the adapter layer, which must not depend
/// on the app's logger). Shared by the J.U.S.T. suite; the folder name (e.g. "JustPlay" / "JustStream")
/// is passed in.
///
/// Retention: files older than <see cref="RetentionDays"/> are pruned on startup — scoped strictly to OUR
/// folder and OUR <c>session-*.log</c> pattern, never anything else (Chloe 2026-07-05: 7 days).
///
/// Robustness: every operation is wrapped so a logging failure (disk full, locked file, denied path) can
/// NEVER take the app down — the show must go on even if it can't write its own log.
/// </summary>
public sealed class SessionLog : ISessionLog
{
    /// <summary>Session logs older than this many days are deleted on startup.</summary>
    public const int RetentionDays = 7;

    private readonly string _dir;
    private readonly object _gate = new();
    private volatile bool _reportedFailure;

    /// <inheritdoc/>
    public Action<string>? OnWriteFailed { get; set; }

    /// <param name="appFolderName">The <c>%LOCALAPPDATA%</c> subfolder to write into (e.g. "JustPlay").</param>
    public SessionLog(string appFolderName)
    {
        _dir = JustDataPaths.Combine(appFolderName);
        Prune();
    }

    /// <summary>Append one already-formatted log line to today's session file.</summary>
    public void Append(string line)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, $"session-{DateTime.Now:yyyy-MM-dd}.log");
            lock (_gate)
                File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Logging must never crash the app — the in-memory log + Console still have it. Surface the
            // FIRST failure to the log window, then stay quiet (a full disk would otherwise spam a line
            // per entry). Reporting itself is wrapped — it can never throw here.
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                try { OnWriteFailed?.Invoke($"Session log could not be written ({ex.Message}) — logging to this window only from here."); }
                catch { /* the error reporter must never become the error */ }
            }
        }
    }

    /// <summary>
    /// Delete OUR OWN <c>session-*.log</c> files older than <see cref="RetentionDays"/> days. Deliberately
    /// narrow: only our folder, only our filename pattern, only by age — it must be impossible for this to
    /// touch a user file. Any single failure is swallowed and pruning continues.
    /// </summary>
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "session-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* one locked / vanished file must not stop pruning the rest */ }
            }
        }
        catch
        {
            // never throw from the constructor path
        }
    }
}
