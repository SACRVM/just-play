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
        _engine.Load(track.FilePath);
        _engine.NormalizationGainDb = ComputeGainDb(track);   // applied to the freshly-loaded source
        _engine.Play();
        CurrentTrackChanged?.Invoke(this, track);
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
        CurrentTrack = next;
        _engine.CrossfadeTo(next.FilePath, ComputeGainDb(next), fadeMs);
        CurrentTrackChanged?.Invoke(this, next);
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
    /// Run <paramref name="whileReleased"/> with <paramref name="track"/>'s file handle
    /// released, then reload and restore the prior playhead + play/pause state. Used by the
    /// tag writer: BASS keeps the file open while it's the current track, so a write would
    /// fail with a sharing violation — this briefly unloads (pause), runs the write, and
    /// resumes from the same spot. If <paramref name="track"/> isn't the current track there
    /// is no handle to release, so the action just runs directly.
    /// </summary>
    public void WithFileReleased(Track track, Action whileReleased)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(whileReleased);

        if (!ReferenceEquals(CurrentTrack, track))
        {
            whileReleased();
            return;
        }

        var pos = _engine.Position;
        var wasPlaying = _engine.State == PlaybackState.Playing;
        _engine.Unload();
        try
        {
            whileReleased();
        }
        finally
        {
            _engine.Load(track.FilePath);
            _engine.Position = pos;
            if (wasPlaying) _engine.Play();
        }
    }

    public void Dispose() => _engine.Dispose();
}
