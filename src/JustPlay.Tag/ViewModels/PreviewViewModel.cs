using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// The listen-while-you-tag preview.
///
/// <para><b>Why it exists:</b> mp3tag cannot play anything — it hands the file to whatever the OS
/// has registered, which means leaving the tagger to hear the track you are tagging. We have an
/// engine, so the preview lives in the pane (Chloe 2026-08-05).</para>
///
/// <para><b>What it deliberately is not:</b> a player. No queue, no crossfade, no DSP rack, no
/// device picker — Load, Play, Pause, Seek. JUST PLAY is the player; this is the ear you need while
/// your hands are in the tags.</para>
///
/// <para><b>The one hard rule:</b> a file being previewed is a file with an open handle, and a tag
/// write on an open handle fails. <see cref="ReleaseIfPlaying"/> is what the save path calls first,
/// so saving never has to be explained away — see <c>TaggerViewModel</c>'s write executor.</para>
/// </summary>
public sealed class PreviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly DispatcherTimer _tick;

    public PreviewViewModel(IAudioEngine engine)
    {
        _engine = engine;

        // 200 ms, the same UI-rate the rest of the suite polls at — fast enough for a moving
        // playhead, slow enough that it is invisible on the CPU.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => Poll();

        _engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Poll);
        _engine.PlaybackEnded += (_, _) => Dispatcher.UIThread.Post(Stop);
    }

    private string? _path;
    /// <summary>The file currently loaded into the preview, or null.</summary>
    public string? Path { get => _path; private set { Set(ref _path, value); Raise(nameof(HasTrack)); } }

    public bool HasTrack => _path is not null;

    private bool _playing;
    public bool IsPlaying { get => _playing; private set => Set(ref _playing, value); }

    private double _position;
    /// <summary>Playhead in seconds. Settable — the slider writes here and the engine follows.</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (Math.Abs(_position - value) < 0.05) return;
            Set(ref _position, value);
            if (HasTrack) _engine.Position = TimeSpan.FromSeconds(value);
            Raise(nameof(Elapsed));
        }
    }

    private double _duration;
    public double Duration { get => _duration; private set => Set(ref _duration, value); }

    public string Elapsed => Format(_position);
    public string Total => Format(_duration);

    /// <summary>
    /// Play <paramref name="path"/>, or toggle pause when it is already the loaded one. Loading a
    /// different file replaces the preview: two tracks at once is a DJ feature, not a tagging one.
    /// </summary>
    public void Toggle(string path)
    {
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
        {
            if (IsPlaying) { _engine.Pause(); IsPlaying = false; _tick.Stop(); }
            else { _engine.Play(); IsPlaying = true; _tick.Start(); }
            return;
        }

        try
        {
            _engine.Load(path);
            _engine.Play();
            Path = path;
            Duration = _engine.Duration.TotalSeconds;
            IsPlaying = true;
            _tick.Start();
        }
        catch (Exception)
        {
            // An unreadable / unsupported file must not take the window down; the preview simply
            // stays empty and the tags are still editable.
            Stop();
        }
    }

    /// <summary>Stop and let go of the file.</summary>
    public void Stop()
    {
        _tick.Stop();
        try { _engine.Stop(); _engine.Unload(); } catch (Exception) { /* already gone */ }
        IsPlaying = false;
        Path = null;
        Position = 0;
        Duration = 0;
    }

    /// <summary>
    /// Let go of <paramref name="path"/> if that is what is playing, so the file can be written.
    /// Returns true when the preview actually released something — the caller may want to say so.
    /// </summary>
    public bool ReleaseIfPlaying(string path)
    {
        if (!string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return false;
        Stop();
        return true;
    }

    private void Poll()
    {
        if (!HasTrack) return;
        _position = _engine.Position.TotalSeconds;
        Raise(nameof(Position));
        Raise(nameof(Elapsed));
        if (_duration <= 0) Duration = _engine.Duration.TotalSeconds;
    }

    private static string Format(double seconds) =>
        seconds <= 0 ? "0:00" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public void Dispose()
    {
        _tick.Stop();
        _engine.Dispose();
    }

    // ── INPC ────────────────────────────────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Raise(name!);
    }
}
