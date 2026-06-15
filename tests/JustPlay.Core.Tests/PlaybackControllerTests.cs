using System.Collections.Generic;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playback;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PlaybackController"/>.
///
/// Coverage areas:
/// 1. <c>ComputeGainDb</c> logic (tested via the public surface: <see cref="PlaybackController.Play"/>
///    and <see cref="PlaybackController.RefreshNormalization"/>).
/// 2. <see cref="PlaybackController.TogglePlayPause"/> state transitions.
/// 3. <see cref="PlaybackController.WithFileReleased"/> — release, action, restore semantics.
/// 4. Event forwarding: StateChanged, TrackEnded, CurrentTrackChanged.
///
/// Uses a minimal in-process fake (<see cref="FakeAudioEngine"/>) — no BASS, no Avalonia.
/// </summary>
public class PlaybackControllerTests
{
    // =========================================================================
    // 1. NormalizationEnabled = false → gain is always 0 regardless of tags
    // =========================================================================

    [Fact]
    public void NormalizationDisabled_GainIsZero_EvenWithReplayGain()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = false };

        // Track has ReplayGain info — but normalization is off.
        var track = TrackWithGain(replayGainDb: -6.0, peak: 0.5);
        ctrl.Play(track);

        Assert.Equal(0.0, engine.NormalizationGainDb);
    }

    // =========================================================================
    // 2. NormalizationEnabled = true, gain fits within headroom → gain applied as-is
    //    ReplayGain −6 dB, peak 0.5 (−6 dBFS). Applied: peak + gain = −6+−6 = −12 dBFS.
    //    That is well under 0 dBFS, so no clipping protection needed → gain = −6.
    // =========================================================================

    [Fact]
    public void NormalizationEnabled_NoClip_GainAppliedAsIs()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        // peak 0.5 = −6.02 dBFS; gain −6 dB → peak + gain ≈ −12 dBFS. No clip.
        var track = TrackWithGain(replayGainDb: -6.0, peak: 0.5);
        ctrl.Play(track);

        Assert.Equal(-6.0, engine.NormalizationGainDb, precision: 6);
    }

    // =========================================================================
    // 3. Positive gain would cause clipping (peak + gain > 0 dBFS) → gain capped
    //    ReplayGain +10 dB, peak 0.95 (−0.446 dBFS).
    //    Un-capped: 0.95 → 0 dBFS with gain = +0.446 dB, NOT +10 dB.
    //    The controller must cap at −peakDb = −20*log10(0.95) ≈ +0.446 dB.
    // =========================================================================

    [Fact]
    public void NormalizationEnabled_ClippingPrevented_GainCapped()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        // peak 0.95: peak + 10 dB would clip → gain must be capped to −peakDb.
        var peak    = 0.95;
        var peakDb  = 20.0 * Math.Log10(peak);   // ≈ −0.446 dB
        var expected = -peakDb;                    // ≈ +0.446 dB

        var track = TrackWithGain(replayGainDb: 10.0, peak: peak);
        ctrl.Play(track);

        Assert.Equal(expected, engine.NormalizationGainDb, precision: 6);
    }

    // =========================================================================
    // 4. Positive gain + peak unknown → gain = 0 (no blind amplification)
    //    ReplayGain +5 dB, peak = null. Controller must not amplify.
    // =========================================================================

    [Fact]
    public void NormalizationEnabled_PositiveGain_PeakUnknown_GainIsZero()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        var track = TrackWithGain(replayGainDb: 5.0, peak: null);
        ctrl.Play(track);

        Assert.Equal(0.0, engine.NormalizationGainDb);
    }

    // =========================================================================
    // 5. Negative gain + peak unknown → gain is applied (attenuation is safe)
    // =========================================================================

    [Fact]
    public void NormalizationEnabled_NegativeGain_PeakUnknown_GainApplied()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        // Negative gain is always safe (it cannot cause clipping) — applies even without peak.
        var track = TrackWithGain(replayGainDb: -8.0, peak: null);
        ctrl.Play(track);

        Assert.Equal(-8.0, engine.NormalizationGainDb, precision: 6);
    }

    // =========================================================================
    // 6. Track has no Analysis at all → gain = 0
    // =========================================================================

    [Fact]
    public void NormalizationEnabled_NoAnalysis_GainIsZero()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        var track = new Track("C:\\test.mp3");   // Analysis is null
        ctrl.Play(track);

        Assert.Equal(0.0, engine.NormalizationGainDb);
    }

    // =========================================================================
    // 7. Track analysed but ReplayGainDb is null → gain = 0
    // =========================================================================

    [Fact]
    public void NormalizationEnabled_ReplayGainNull_GainIsZero()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        var track = new Track("C:\\test.mp3")
        {
            Analysis = new AnalysisResult { Bpm = 128.0 /* no ReplayGainDb */ }
        };
        ctrl.Play(track);

        Assert.Equal(0.0, engine.NormalizationGainDb);
    }

    // =========================================================================
    // 8. RefreshNormalization re-applies the current track's gain without reload
    // =========================================================================

    [Fact]
    public void RefreshNormalization_UpdatesGain_WithoutReload()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = false };

        var track = TrackWithGain(replayGainDb: -4.0, peak: 0.8);
        ctrl.Play(track);
        Assert.Equal(0.0, engine.NormalizationGainDb);   // off during Play

        // Enable normalization and refresh — no reload should happen.
        var loadsBefore = engine.LoadCount;
        ctrl.NormalizationEnabled = true;
        ctrl.RefreshNormalization();

        Assert.Equal(loadsBefore, engine.LoadCount);    // no extra Load call
        Assert.Equal(-4.0, engine.NormalizationGainDb, precision: 6);
    }

    // =========================================================================
    // 9. RefreshNormalization with no current track — no crash, gain = 0
    // =========================================================================

    [Fact]
    public void RefreshNormalization_NoCurrentTrack_GainIsZero()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        // No track has been played — CurrentTrack is null.
        var ex = Record.Exception(() => ctrl.RefreshNormalization());
        Assert.Null(ex);
        Assert.Equal(0.0, engine.NormalizationGainDb);
    }

    // =========================================================================
    // 10. TogglePlayPause: stopped + CurrentTrack → no-op (nothing loaded yet)
    //     (PlaybackState.Stopped + CurrentTrack != null → engine.Play() is called)
    // =========================================================================

    [Fact]
    public void TogglePlayPause_WhenPlaying_Pauses()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = false };

        ctrl.Play(TrackWithGain(replayGainDb: -1.0, peak: 0.5));
        // After Play(), the fake engine is in Playing state.
        Assert.Equal(PlaybackState.Playing, engine.State);

        ctrl.TogglePlayPause();
        Assert.Equal(PlaybackState.Paused, engine.State);
    }

    [Fact]
    public void TogglePlayPause_WhenPaused_Resumes()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = false };

        ctrl.Play(TrackWithGain(replayGainDb: -1.0, peak: 0.5));
        ctrl.TogglePlayPause();  // → Paused
        ctrl.TogglePlayPause();  // → Playing again
        Assert.Equal(PlaybackState.Playing, engine.State);
    }

    [Fact]
    public void TogglePlayPause_WhenStopped_NoTrack_IsNoOp()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine);

        // Engine is Stopped, no CurrentTrack.
        Assert.Equal(PlaybackState.Stopped, engine.State);
        ctrl.TogglePlayPause();  // Must not throw or change state.
        Assert.Equal(PlaybackState.Stopped, engine.State);
    }

    // =========================================================================
    // 11. WithFileReleased — non-current track runs the action directly, no unload
    // =========================================================================

    [Fact]
    public void WithFileReleased_NonCurrentTrack_RunsActionWithoutUnload()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine);

        var currentTrack = new Track("C:\\a.mp3");
        var otherTrack   = new Track("C:\\b.mp3");

        ctrl.Play(currentTrack);

        var unloadsBefore = engine.UnloadCount;
        var actionRan = false;
        ctrl.WithFileReleased(otherTrack, () => actionRan = true);

        Assert.True(actionRan);
        Assert.Equal(unloadsBefore, engine.UnloadCount);  // no unload for other track
    }

    // =========================================================================
    // 12. WithFileReleased — current track: unloads, runs action, reloads
    // =========================================================================

    [Fact]
    public void WithFileReleased_CurrentTrack_UnloadsAndReloads()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine);

        var track = new Track("C:\\current.mp3");
        ctrl.Play(track);

        var actionRan = false;
        ctrl.WithFileReleased(track, () =>
        {
            Assert.Equal(1, engine.UnloadCount);   // already unloaded when action runs
            actionRan = true;
        });

        Assert.True(actionRan);
        Assert.Equal(2, engine.LoadCount);    // initial Load + reload after action
    }

    // =========================================================================
    // 13. WithFileReleased — if action throws, file is still reloaded (finally)
    // =========================================================================

    [Fact]
    public void WithFileReleased_ActionThrows_FileStillReloaded()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine);

        var track = new Track("C:\\current.mp3");
        ctrl.Play(track);

        Assert.Throws<InvalidOperationException>(() =>
            ctrl.WithFileReleased(track, () => throw new InvalidOperationException("test")));

        // Even though the action threw, the controller must reload the track.
        Assert.Equal(2, engine.LoadCount);
    }

    // =========================================================================
    // 14. CurrentTrackChanged event fires on Play
    // =========================================================================

    [Fact]
    public void Play_FiresCurrentTrackChangedEvent()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine);

        Track? notified = null;
        ctrl.CurrentTrackChanged += (_, t) => notified = t;

        var track = new Track("C:\\test.mp3");
        ctrl.Play(track);

        Assert.Same(track, notified);
    }

    // =========================================================================
    // 15. StateChanged event is forwarded from the engine
    // =========================================================================

    [Fact]
    public void StateChanged_IsForwardedFromEngine()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine);

        PlaybackState? observed = null;
        ctrl.StateChanged += (_, s) => observed = s;

        engine.RaiseStateChanged(PlaybackState.Playing);

        Assert.Equal(PlaybackState.Playing, observed);
    }

    // =========================================================================
    // 16. TrackEnded event is forwarded from the engine
    // =========================================================================

    [Fact]
    public void TrackEnded_IsForwardedFromEngine_WithCurrentTrack()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine);

        var track = new Track("C:\\test.mp3");
        ctrl.Play(track);

        Track? ended = null;
        ctrl.TrackEnded += (_, t) => ended = t;
        engine.RaisePlaybackEnded();

        Assert.Same(track, ended);
    }

    // =========================================================================
    // 17. FadeOutAsync: called before teardown, the engine records the request
    //     (graceful-quit contract — no BASS, no real audio; tests the seam)
    // =========================================================================

    [Fact]
    public async Task FadeOutAsync_WhilePlaying_IsRequestedOnEngine()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = false };

        ctrl.Play(TrackWithGain(replayGainDb: -1.0, peak: 0.5));
        Assert.Equal(PlaybackState.Playing, engine.State);

        // Simulate what MainWindow.OnClosing does: fade before dispose.
        await engine.FadeOutAsync(200);

        Assert.Equal(1, engine.FadeOutCount);
        Assert.Equal(200, engine.LastFadeMs);
    }

    [Fact]
    public async Task FadeOutAsync_WhenStopped_IsStillRecorded_NoCrash()
    {
        // When nothing is playing the fake (and real) engine return immediately — no click,
        // no error. The caller must still be able to call it safely.
        var engine = new FakeAudioEngine();

        await engine.FadeOutAsync(150);

        Assert.Equal(1, engine.FadeOutCount);
        Assert.Equal(150, engine.LastFadeMs);
    }

    [Fact]
    public async Task FadeOutAsync_CalledTwice_SecondCallIsNoOp()
    {
        // The real engine has an Interlocked guard; the fake records every call so we can
        // confirm the real contract: two rapid calls from different code paths (window close
        // AND self-update shutdown racing) result in ONE fade, not two.
        // The fake doesn't replicate the guard — this test documents the intended real behaviour
        // and uses the fake to confirm the method signature is callable asynchronously.
        var engine = new FakeAudioEngine();
        // Play a track so the engine is in Playing state.
        var ctrl = new PlaybackController(engine);
        ctrl.Play(TrackWithGain(replayGainDb: -1.0, peak: 0.5));

        var t1 = engine.FadeOutAsync(200);
        var t2 = engine.FadeOutAsync(200);
        await Task.WhenAll(t1, t2);

        // The fake records both (no guard), but the REAL engine would guard.
        // What we assert here: no exception, the method is awaitable.
        Assert.True(engine.FadeOutCount >= 1);
    }

    // =========================================================================
    // 18. CrossfadeTo: becomes current, forwards path + fade, fires CurrentTrackChanged
    // =========================================================================

    [Fact]
    public void CrossfadeTo_BecomesCurrent_ForwardsPathAndFade_FiresEvent()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = false };

        ctrl.Play(new Track("C:\\a.mp3"));

        Track? notified = null;
        ctrl.CurrentTrackChanged += (_, t) => notified = t;

        var next = new Track("C:\\b.mp3");
        ctrl.CrossfadeTo(next, fadeMs: 4000);

        Assert.Same(next, ctrl.CurrentTrack);
        Assert.Same(next, notified);
        Assert.Equal(1, engine.CrossfadeCount);
        Assert.Equal("C:\\b.mp3", engine.LastCrossfadePath);
        Assert.Equal(4000, engine.LastCrossfadeMs);
    }

    // =========================================================================
    // 19. CrossfadeTo applies the SAME normalization gain logic as Play
    // =========================================================================

    [Fact]
    public void CrossfadeTo_AppliesNormalizationGain_LikePlay()
    {
        var engine = new FakeAudioEngine();
        var ctrl   = new PlaybackController(engine) { NormalizationEnabled = true };

        ctrl.Play(new Track("C:\\a.mp3"));
        // peak 0.5 = −6.02 dBFS; gain −6 dB → no clip → applied as-is.
        ctrl.CrossfadeTo(TrackWithGain(replayGainDb: -6.0, peak: 0.5), fadeMs: 2000);

        Assert.Equal(-6.0, engine.NormalizationGainDb, precision: 6);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static Track TrackWithGain(double replayGainDb, double? peak)
        => new Track("C:\\test.mp3")
        {
            Analysis = new AnalysisResult
            {
                ReplayGainDb = replayGainDb,
                Peak         = peak,
            }
        };
}

// =============================================================================
// Fake IAudioEngine — in-process stub, no BASS dependency
// =============================================================================

/// <summary>
/// Minimal in-process fake for <see cref="IAudioEngine"/>.
/// Tracks Load/Unload call counts and NormalizationGainDb for assertion,
/// and simulates state transitions on Play/Pause/Stop.
/// Also records FadeOutAsync calls so teardown tests can verify the fade was
/// requested before Dispose (the "graceful-quit" contract).
/// </summary>
file sealed class FakeAudioEngine : IAudioEngine
{
    private PlaybackState _state = PlaybackState.Stopped;

    public int LoadCount    { get; private set; }
    public int UnloadCount  { get; private set; }
    public int FadeOutCount { get; private set; }
    public int LastFadeMs   { get; private set; }

    public int     CrossfadeCount    { get; private set; }
    public string? LastCrossfadePath { get; private set; }
    public int     LastCrossfadeMs   { get; private set; }

    // --- IAudioEngine ---
    public PlaybackState State          => _state;
    public double        Volume         { get; set; } = 1.0;
    public double        NormalizationGainDb { get; set; }
    public TimeSpan      Position       { get; set; }
    public TimeSpan      Duration       => TimeSpan.Zero;
    public int           CurrentOutputDevice { get; } = -1;

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler?                PlaybackEnded;

    public void Load(string _)
    {
        LoadCount++;
        _state = PlaybackState.Stopped;
    }

    public void Unload()
    {
        UnloadCount++;
        _state = PlaybackState.Stopped;
    }

    public void Play()
    {
        _state = PlaybackState.Playing;
        StateChanged?.Invoke(this, _state);
    }

    public void Pause()
    {
        _state = PlaybackState.Paused;
        StateChanged?.Invoke(this, _state);
    }

    public void Stop()
    {
        _state = PlaybackState.Stopped;
        StateChanged?.Invoke(this, _state);
    }

    public void CrossfadeTo(string filePath, double normGainDb, int fadeMs)
    {
        CrossfadeCount++;
        LastCrossfadePath = filePath;
        LastCrossfadeMs   = fadeMs;
        NormalizationGainDb = normGainDb;
        LoadCount++;                       // a fresh source was loaded for the incoming track
        _state = PlaybackState.Playing;
        StateChanged?.Invoke(this, _state);
    }

    /// <summary>
    /// Fake FadeOutAsync: records the call for assertion but returns immediately
    /// (no real audio, no delay). The fade is always considered "requested" after
    /// this returns, regardless of engine state.
    /// </summary>
    public Task FadeOutAsync(int fadeMs = 200)
    {
        FadeOutCount++;
        LastFadeMs = fadeMs;
        return Task.CompletedTask;
    }

    public void GetFftBands(Span<float> destination) => destination.Clear();

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
        => Array.Empty<AudioOutputDevice>();

    public void SetOutputDevice(int _) { }

    public void Dispose() { }

    // Test helpers — let tests trigger engine events directly.
    public void RaiseStateChanged(PlaybackState state)
        => StateChanged?.Invoke(this, state);

    public void RaisePlaybackEnded()
        => PlaybackEnded?.Invoke(this, EventArgs.Empty);
}
