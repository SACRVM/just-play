using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// The listen-while-you-tag preview.
///
/// <para><b>Why it exists:</b> mp3tag cannot play anything - it hands the file to whatever the OS
/// has registered, which means leaving the tagger to hear the track you are tagging. We have an
/// engine, so the preview lives in the pane (Chloe 2026-08-05).</para>
///
/// <para><b>What it deliberately is not:</b> a player. No queue, no crossfade, no DSP rack, no
/// device picker - Load, Play, Pause, Seek. JUST PLAY is the player; this is the ear you need while
/// your hands are in the tags.</para>
///
/// <para><b>The one hard rule:</b> a file being previewed is a file with an open handle, and a tag
/// write on an open handle fails. <see cref="ReleaseIfPlaying"/> is what the save path calls first,
/// so saving never has to be explained away - see <c>TaggerViewModel</c>'s write executor.</para>
/// </summary>
public sealed class PreviewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly DispatcherTimer _tick;

    public PreviewViewModel(IAudioEngine engine)
    {
        _engine = engine;

        // 200 ms, the same UI-rate the rest of the suite polls at - fast enough for a moving
        // playhead, slow enough that it is invisible on the CPU.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => Poll();

        _engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Poll);
        _engine.PlaybackEnded += (_, _) => Dispatcher.UIThread.Post(Stop);
    }

    private string? _path;
    /// <summary>The file currently loaded into the preview, or null.</summary>
    public string? Path
    {
        get => _path;
        private set
        {
            Set(ref _path, value);
            foreach (var n in (string[])[nameof(HasTrack), nameof(TrackName), nameof(Dim)]) Raise(n);
        }
    }

    public bool HasTrack => _path is not null;

    /// <summary>The player strip DIMS when nothing is loaded instead of disappearing - a control that
    /// vanishes makes the layout jump, and an empty transport still tells you where the transport is.</summary>
    public double Dim => HasTrack ? 1.0 : 0.55;

    private bool _playing;
    public bool IsPlaying { get => _playing; private set => Set(ref _playing, value); }

    private double _position;
    /// <summary>Playhead in seconds. Settable - the slider writes here and the engine follows.</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (Math.Abs(_position - value) < 0.05) return;
            Set(ref _position, value);
            Raise(nameof(Elapsed));

            // Only a USER move seeks. _suppressSeek marks the poll's own write-back - see Poll().
            if (_suppressSeek || !HasTrack) return;
            _engine.Position = TimeSpan.FromSeconds(value);
            _pendingSeek = value;      // hold this target until the engine reports it
            _pendingSeekTicks = 0;
        }
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        private set { Set(ref _duration, value); Raise(nameof(SeekMax)); }
    }

    /// <summary>What the seek slider uses as its maximum - never zero.
    ///
    /// <para>(!) Binding a Slider's Maximum straight to <see cref="Duration"/> is a trap: with nothing
    /// loaded that is 0, so Minimum == Maximum, and Avalonia has no ratio to place the thumb by - it
    /// parks it at the END. The empty player then showed a full progress bar next to "0:00"
    /// (Chloe 2026-08-06). A floor of 1 keeps the thumb where "nothing has played yet" belongs.</para></summary>
    public double SeekMax => _duration > 0 ? _duration : 1;

    public string Elapsed => Format(_position);
    public string Total => Format(_duration);

    /// <summary>What is loaded, in words. The transport lives on the far side of the window from the
    /// file list, and the cursor can move away from the playing file - so the player has to say what
    /// it is playing rather than leave it to be inferred from a highlighted row (Chloe 2026-08-06).
    /// The file NAME, not the title tag: a tagger is often looking at files whose tags are wrong or
    /// missing, and the name is the thing that is always there.</summary>
    public string? TrackName =>
        _path is null ? null : System.IO.Path.GetFileNameWithoutExtension(_path);

    private double _volume = 1.0;

    /// <summary>Preview loudness, 0...1. Not persisted on purpose - it is an audition level for this
    /// session, not a setting, and JUST TAG never touches the app's playback volume.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(_volume - v) < 0.001) return;
            Set(ref _volume, v);
            try { _engine.Volume = v; }
            catch (Exception) { /* a preview level is never worth taking the window down for */ }
        }
    }

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
        // Path first: it clears HasTrack, so the Position write below cannot seek an engine that has
        // just been unloaded - and it drops any seek target the previous file was still waiting on.
        Path = null;
        _pendingSeek = null;
        _pendingSeekTicks = 0;
        Position = 0;
        Duration = 0;
    }

    /// <summary>
    /// Let go of <paramref name="path"/> if that is what is playing, so the file can be written.
    /// Returns true when the preview actually released something - the caller may want to say so.
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

        var pos = _engine.Position.TotalSeconds;

        // (!) USER-SEEK RECONCILIATION - the "drags and it jumps back, then forward" bug, ported from
        // MainWindowViewModel where it was solved once already (Chloe 2026-08-06: "alter bug neu
        // erfunden ... das hatten wir frueher bei just play auch").
        //
        // Dragging the thumb writes Position -> the native engine seeks. BASS needs a moment before it
        // reports the new position, so the very next 200 ms tick reads the OLD one and writes it back
        // into the slider - the thumb snaps behind the pointer, then catches up. Holding the target
        // until the engine actually arrives near it is the fix; the tick budget bounds the wait so a
        // target that is never reached (end of file, a decode hiccup) cannot freeze the thumb.
        if (_pendingSeek is { } target)
        {
            if (Math.Abs(pos - target) <= 0.75 || ++_pendingSeekTicks >= 5) _pendingSeek = null;
            else { Raise(nameof(Elapsed)); return; }   // keep showing the target, not the stale position
        }

        // (!) And WITHOUT the suppress flag this assignment would run the setter's seek - the poll would
        // order the engine to jump to where it just said it was, 5x a second, forever.
        _suppressSeek = true;
        Position = pos;
        _suppressSeek = false;

        if (_duration <= 0) Duration = _engine.Duration.TotalSeconds;
    }

    private double? _pendingSeek;
    private int _pendingSeekTicks;
    private bool _suppressSeek;

    private static string Format(double seconds) =>
        seconds <= 0 ? "0:00" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public void Dispose()
    {
        _tick.Stop();
        _engine.Dispose();
    }

    // -- INPC ------------------------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Raise(name!);
    }
}
