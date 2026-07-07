using System;
using System.IO;

namespace JustPlay.Stream.Logging;

/// <summary>
/// Persists the JUST STREAM event log to a rolling daily file in <c>%LOCALAPPDATA%\JustStream</c>
/// (<c>session-YYYY-MM-DD.log</c>) so a DJ can review a set afterwards — when the connection
/// dropped, when it reconnected, what was recorded. This is the SAME feed shown live in the log
/// window (<see cref="ViewModels.StreamViewModel.Log"/>), not the low-level BASS console
/// diagnostics (those live in the adapter layer, which must not depend on the app's logger).
///
/// Retention: files older than <see cref="RetentionDays"/> are pruned on startup — scoped strictly
/// to OUR folder and OUR <c>session-*.log</c> pattern, never anything else (Chloe 2026-07-05: 7 days).
///
/// Robustness: every operation is wrapped so a logging failure (disk full, locked file, denied path)
/// can NEVER take the broadcaster down — the show must go on even if it can't write its own log.
/// </summary>
public sealed class SessionLog
{
    /// <summary>Session logs older than this many days are deleted on startup.</summary>
    public const int RetentionDays = 7;

    private readonly string _dir;
    private readonly object _gate = new();
    private volatile bool _reportedFailure;

    /// <summary>
    /// Invoked ONCE (per session) with a human message when a log write fails — so a disk-full /
    /// denied-path condition surfaces in the log WINDOW instead of dying silently, honouring the
    /// rule "storage never crashes; worst case it's only logged to the window" (Chloe 2026-07-05).
    /// The handler must NOT try to persist (that's exactly what just failed) — see
    /// <see cref="ViewModels.StreamViewModel.LogToWindowOnly"/>.
    /// </summary>
    public Action<string>? OnWriteFailed { get; set; }

    public SessionLog()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustStream");
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
            // Logging must never crash the broadcaster — the in-memory log + Console still have it.
            // Surface the FIRST failure to the log window, then stay quiet (a full disk would
            // otherwise spam a line per entry). Reporting itself is wrapped — it can never throw here.
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                try { OnWriteFailed?.Invoke($"Session log could not be written ({ex.Message}) — logging to this window only from here."); }
                catch { /* the error reporter must never become the error */ }
            }
        }
    }

    /// <summary>
    /// Delete OUR OWN <c>session-*.log</c> files older than <see cref="RetentionDays"/> days.
    /// Deliberately narrow: only our folder, only our filename pattern, only by age — it must be
    /// impossible for this to touch a user file. Any single failure is swallowed and pruning continues.
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
