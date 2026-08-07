using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using JustPlay.Core.Logging;

namespace JustPlay.UI.Logging;

/// <summary>
/// The SHARED in-app event log - the memory feed behind the suite's <see cref="Views.LogWindow"/> (both
/// JUST PLAY and JUST STREAM own an instance). <see cref="Append"/> is thread-safe (marshals to the UI
/// thread), capped at <see cref="MaxEntries"/> lines, and optionally mirrored to a daily session file via
/// <see cref="ISessionLog"/>. This is the app's "don't swallow errors silently" channel: a file-lock /
/// tag-write / storage failure gets a visible, copyable line here instead of a lost <c>Console.WriteLine</c>.
///
/// Hand-rolled <see cref="INotifyPropertyChanged"/> on purpose - the shared UI library must stay free of a
/// MVVM-toolkit dependency (see JustPlay.UI.csproj). The window's CLEAR/COPY are code-behind, so no ICommand.
/// </summary>
public sealed class LogViewModel : INotifyPropertyChanged
{
    private const int MaxEntries = 200;
    private readonly ISessionLog? _sessionLog;

    public ObservableCollection<string> Entries { get; } = new();

    /// <summary>All lines joined - bound read-only by the LogWindow's selectable text box + COPY button.</summary>
    public string Text => string.Join(Environment.NewLine, Entries);

    private bool _hasUnread;
    /// <summary>Drives the unread dot on the chrome log button; cleared by <see cref="MarkRead"/> on open.</summary>
    public bool HasUnread
    {
        get => _hasUnread;
        private set { if (_hasUnread != value) { _hasUnread = value; Raise(); } }
    }

    public LogViewModel(ISessionLog? sessionLog = null)
    {
        _sessionLog = sessionLog;
        Entries.CollectionChanged += (_, _) => Raise(nameof(Text));
        // A session-file write failure surfaces in the window ONLY (re-persisting would hit the same full
        // disk and recurse) - the same contract JUST STREAM had.
        if (_sessionLog is not null) _sessionLog.OnWriteFailed = AppendMemoryOnly;
    }

    /// <summary>Append a message: timestamp it, cap the buffer, mark unread, mirror to Console + the
    /// session file. Safe to call from any thread.</summary>
    public void Append(string message) => OnUi(() =>
    {
        var line = Stamp(message);
        AddCapped(line);
        Console.WriteLine(line);
        _sessionLog?.Append(line);
    });

    /// <summary>Window-only append (no session-file persist) - for reporting a STORAGE failure without
    /// recursing into the writer that just failed.</summary>
    public void AppendMemoryOnly(string message) => OnUi(() =>
    {
        var line = Stamp(message);
        AddCapped(line);
        Console.WriteLine(line);
    });

    /// <summary>Called by the LogWindow on open - clears the unread marker.</summary>
    public void MarkRead() => OnUi(() => HasUnread = false);

    /// <summary>Clears the in-memory feed (the CLEAR button). The session file is left intact.</summary>
    public void Clear() => OnUi(Entries.Clear);

    private static string Stamp(string message) => $"{DateTime.Now:HH:mm:ss}  {message}";

    private void AddCapped(string line)
    {
        Entries.Add(line);
        if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        HasUnread = true;
    }

    // Run on the UI thread: synchronously if we're already on it (preserves call order for UI callers),
    // else post (so a background writer - e.g. a locked-file tag-write on a worker thread - is safe).
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
