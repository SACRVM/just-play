using System.Collections.Generic;
using System.Linq;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.Core.Playback;

/// <summary>
/// Sits on top of <see cref="IAudioEngine"/> and adds the notion of a "current track".
/// Pure logic, no UI framework — the ViewModel subscribes to its events.
/// Embodies the JustPlay rule: double-click a track → it plays. No queue memory.
/// </summary>
public sealed class PlaybackController : IDisposable
{
    private readonly IAudioEngine _engine;

    // Tag writes requested for the CURRENTLY-PLAYING track are deferred here instead of unloading
    // the track to free its file handle (the old approach interrupted playback and could trip the
    // end-of-track handling into advancing the queue). They run the moment the track is no longer
    // current (a track change frees the handle — see FlushDeferredWrites) or on app exit
    // (FlushPendingWrites). Guarded by a lock: writes can be queued from a background analysis thread.
    private readonly object _deferLock = new();
    private readonly List<(Track track, System.Action action)> _deferred = new();

    public PlaybackController(IAudioEngine engine)
    {
        _engine = engine;
        _engine.StateChanged += (_, s) => StateChanged?.Invoke(this, s);
        _engine.PlaybackEnded += (_, _) => TrackEnded?.Invoke(this, CurrentTrack);
    }

    public Track? CurrentTrack { get; private set; }

    /// <summary>
    /// When true, each played track's ReplayGain is applied on the engine (output volume) so the
    /// queue plays at an even loudness. Set from settings; toggling it mid-playback takes effect
    /// after <see cref="RefreshNormalization"/> (or on the next track). Off → unity gain.
    /// </summary>
    public bool NormalizationEnabled { get; set; }

    /// <summary>
    /// The loudness TARGET for playback normalization, in LUFS. The stored ReplayGain is referenced
    /// to −18 LUFS (the RG 2.0 / tag standard); playback re-references it to this target so the user
    /// can pick how loud the level-matched output sits — Quiet −19 / Normal −14 / Loud −11, mirroring
    /// the streaming players. −18 (the RG reference) means "apply the tag's gain verbatim". Defaults
    /// to −18 so unit tests see the tag value unchanged; the app sets it from the user's level.
    /// </summary>
    public double TargetLufs { get; set; } = ReplayGain.ReferenceLufs;

    public PlaybackState State => _engine.State;

    public TimeSpan Position
    {
        get => _engine.Position;
        set => _engine.Position = value;
    }

    public TimeSpan Duration => _engine.Duration;

    public double Volume
    {
        get => _engine.Volume;
        set => _engine.Volume = value;
    }

    /// <summary>Raised on stop/play/pause transitions.</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised when the current track plays to its end. Carries the track that ended.</summary>
    public event EventHandler<Track?>? TrackEnded;

    public event EventHandler<Track>? CurrentTrackChanged;

    /// <summary>Load and immediately play a track. The core "just play it" path.</summary>
    public void Play(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        CurrentTrack = track;
        _engine.Load(track.FilePath);   // frees the previous source (+ any crossfade tail) → handles released
        _engine.NormalizationGainDb = ComputeGainDb(track);   // applied to the freshly-loaded source
        _engine.Play();
        CurrentTrackChanged?.Invoke(this, track);
        FlushDeferredWrites();          // the track we just left is freed → land its pending tag writes
    }

    /// <summary>
    /// Crossfade from the current track into <paramref name="next"/> over <paramref name="fadeMs"/> ms.
    /// Mirrors <see cref="Play"/>'s bookkeeping (CurrentTrack, normalization, CurrentTrackChanged) but
    /// the outgoing track keeps playing as it fades out — no hard cut. <paramref name="next"/> becomes
    /// the current track immediately; the OLD track does not raise <see cref="TrackEnded"/> (the engine
    /// suppresses it), so the queue advances exactly once per track. With <paramref name="fadeMs"/> ≤ 0
    /// (or nothing playing) the engine degrades this to a plain load-and-play.
    /// </summary>
    public void CrossfadeTo(Track next, int fadeMs)
    {
        ArgumentNullException.ThrowIfNull(next);
        var leaving = CurrentTrack;     // becomes the outgoing deck — its file handle lingers during the fade
        CurrentTrack = next;
        _engine.CrossfadeTo(next.FilePath, ComputeGainDb(next), fadeMs);
        CurrentTrackChanged?.Invoke(this, next);
        // Flush deferred writes for any track that's free now — but NOT for `leaving`, whose handle is
        // still open while it fades out; its pending write lands on the next track change (or on exit).
        FlushDeferredWrites(alsoExclude: leaving);
    }

    /// <summary>Re-apply normalization to the CURRENT track — call after toggling
    /// <see cref="NormalizationEnabled"/> so the change is heard without reloading the track.</summary>
    public void RefreshNormalization() => _engine.NormalizationGainDb = ComputeGainDb(CurrentTrack);

    /// <summary>
    /// The dB gain to apply for <paramref name="t"/>: its ReplayGain when normalization is on,
    /// 0 otherwise. Clipping is prevented — a positive gain is capped so the measured peak lands
    /// at most at full scale; if the peak is unknown we never amplify (only attenuate).
    /// </summary>
    private double ComputeGainDb(Track? t)
    {
        if (!NormalizationEnabled || t?.Analysis is not { ReplayGainDb: { } gain }) return 0.0;
        // Re-reference the −18-LUFS tag gain to the chosen playback target + clip-prevent. Shared with
        // the GAIN-column display so what's shown == what's applied. At TargetLufs = −18 this is the
        // verbatim tag gain (so the unit tests, which use the default, see the tag value unchanged).
        return ReplayGain.AppliedGainDb(gain, t.Analysis.Peak, TargetLufs);
    }

    /// <summary>Toggle play/pause on the current track; no-op if nothing is loaded.</summary>
    public void TogglePlayPause()
    {
        switch (_engine.State)
        {
            case PlaybackState.Playing:
                _engine.Pause();
                break;
            case PlaybackState.Paused:
                _engine.Play();
                break;
            case PlaybackState.Stopped when CurrentTrack is not null:
                _engine.Play();
                break;
        }
    }

    public void Stop() => _engine.Stop();

    /// <summary>
    /// Run a file-mutating action (a tag write) for <paramref name="track"/> with its file handle
    /// free. If the track isn't the one currently loaded, BASS isn't holding it open, so the action
    /// runs immediately. If it IS the playing track, the action is DEFERRED — playback continues
    /// untouched and the write runs the moment the track stops being current (a track change frees
    /// the handle, see <see cref="FlushDeferredWrites"/>) or on app exit (<see cref="FlushPendingWrites"/>).
    /// <para>
    /// This replaced an earlier unload→write→reload approach that briefly interrupted playback and,
    /// during a bulk analyse, tripped the end-of-track handling into jumping the queue to the first song.
    /// </para>
    /// </summary>
    public void WithFileReleased(Track track, Action whileReleased)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(whileReleased);

        // Not the loaded track → no open handle → just write now (may run on a background thread).
        if (!ReferenceEquals(CurrentTrack, track))
        {
            whileReleased();
            return;
        }

        // The currently-playing track: defer; never unload it. Thread-safe (bulk analyse queues
        // these from background threads).
        lock (_deferLock) _deferred.Add((track, whileReleased));
    }

    /// <summary>Run deferred writes for every track whose file handle is now free (i.e. not the
    /// current track, and not <paramref name="alsoExclude"/> — used to skip a track that is still
    /// fading out after a crossfade). Each action is exception-safe on its own.</summary>
    private void FlushDeferredWrites(Track? alsoExclude = null)
    {
        (Track track, System.Action action)[] ready;
        lock (_deferLock)
        {
            ready = _deferred
                .Where(d => !ReferenceEquals(d.track, CurrentTrack)
                         && (alsoExclude is null || !ReferenceEquals(d.track, alsoExclude)))
                .ToArray();
            foreach (var d in ready) _deferred.Remove(d);
        }
        foreach (var d in ready) d.action();
    }

    /// <summary>
    /// Land EVERY pending deferred write, releasing the current track's file handle first. Call once
    /// on app shutdown (after the graceful fade) so a tag write requested for the playing track still
    /// reaches disk — the "write it when the song stops, and on quit" requirement. Best-effort: a
    /// failing write is swallowed (we're tearing down) rather than blocking exit.
    /// </summary>
    public void FlushPendingWrites()
    {
        try { _engine.Unload(); } catch { /* already unloaded / disposed — fine */ }

        (Track track, System.Action action)[] all;
        lock (_deferLock) { all = _deferred.ToArray(); _deferred.Clear(); }
        foreach (var d in all)
        {
            try { d.action(); } catch { /* best-effort on shutdown */ }
        }
    }

    public void Dispose()
    {
        FlushPendingWrites();   // belt-and-braces: land anything still pending if Dispose is the exit path
        _engine.Dispose();
    }
}
