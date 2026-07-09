using System;

namespace JustPlay.Core.Logging;

/// <summary>
/// Persists an app's event log to a rolling daily file for post-session review. Shared by the J.U.S.T.
/// suite (JUST PLAY, JUST STREAM) so the log window and its file backing are written once. The concrete
/// <see cref="SessionLog"/> is parameterised by the app's <c>%LOCALAPPDATA%</c> folder name.
/// </summary>
public interface ISessionLog
{
    /// <summary>Append one already-formatted line to today's session file. Must never throw.</summary>
    void Append(string line);

    /// <summary>
    /// Invoked ONCE per session when a write fails (disk full / denied path), so the condition can surface
    /// in the log WINDOW instead of dying silently. The handler must NOT try to persist — that is exactly
    /// what just failed.
    /// </summary>
    Action<string>? OnWriteFailed { get; set; }
}
