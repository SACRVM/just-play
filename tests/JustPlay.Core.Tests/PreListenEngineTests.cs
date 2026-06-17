using System;
using System.Collections.Generic;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Unit tests for the IPreListenEngine state machine contract.
///
/// Coverage:
/// 1. OutputDevice == -1 means disabled: Load/Play are silent no-ops.
/// 2. Load → Play → state == Playing.
/// 3. Play → Pause → state == Paused (position retained).
/// 4. Play → Stop  → state == Stopped (position reset).
/// 5. Unload releases the track (Duration == 0 after Unload).
/// 6. StateChanged event fires on transitions.
/// 7. PlaybackEnded event fires when the fake triggers it.
/// 8. Volume setter propagates to the engine.
/// 9. GetOutputDevices returns the configured stub list.
///
/// Uses FakePreListenEngine — no BASS, no Avalonia.
/// </summary>
public class PreListenEngineTests
{
    // =========================================================================
    // 1. OutputDevice == -1: Load / Play are silent no-ops
    // =========================================================================

    [Fact]
    public void WhenOutputDeviceIsMinusOne_LoadIsNoOp()
    {
        var engine = new FakePreListenEngine { OutputDevice = -1 };
        engine.Load("some.mp3");

        Assert.Equal(0, engine.LoadCount);
        Assert.Equal(PlaybackState.Stopped, engine.State);
    }

    [Fact]
    public void WhenOutputDeviceIsMinusOne_PlayIsNoOp()
    {
        var engine = new FakePreListenEngine { OutputDevice = -1 };
        engine.Load("some.mp3");
        engine.Play();

        Assert.Equal(PlaybackState.Stopped, engine.State);
    }

    // =========================================================================
    // 2. Load → Play → Playing
    // =========================================================================

    [Fact]
    public void AfterLoad_PlayTransitionsToPlaying()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");
        engine.Play();

        Assert.Equal(PlaybackState.Playing, engine.State);
    }

    [Fact]
    public void Load_SetsNonZeroDuration()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");

        Assert.True(engine.Duration > TimeSpan.Zero);
    }

    // =========================================================================
    // 3. Play → Pause → Paused (position retained)
    // =========================================================================

    [Fact]
    public void Play_ThenPause_StateIsPaused()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");
        engine.Play();
        engine.Position = TimeSpan.FromSeconds(10);
        engine.Pause();

        Assert.Equal(PlaybackState.Paused, engine.State);
        Assert.Equal(TimeSpan.FromSeconds(10), engine.Position);
    }

    // =========================================================================
    // 4. Play → Stop → Stopped and position rewinds
    // =========================================================================

    [Fact]
    public void Stop_ResetsPositionAndStatesToStopped()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");
        engine.Play();
        engine.Position = TimeSpan.FromSeconds(15);
        engine.Stop();

        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(TimeSpan.Zero, engine.Position);
    }

    // =========================================================================
    // 5. Unload releases track → Duration = 0
    // =========================================================================

    [Fact]
    public void Unload_ClearsDuration_AndSetsStopped()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");
        engine.Play();
        engine.Unload();

        Assert.Equal(TimeSpan.Zero, engine.Duration);
        Assert.Equal(PlaybackState.Stopped, engine.State);
    }

    // =========================================================================
    // 6. StateChanged fires on each transition
    // =========================================================================

    [Fact]
    public void StateChanged_FiresOnPlayAndStop()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        var states = new List<PlaybackState>();
        engine.StateChanged += (_, s) => states.Add(s);

        engine.Load("track.mp3");
        engine.Play();
        engine.Stop();

        Assert.Contains(PlaybackState.Playing, states);
        Assert.Contains(PlaybackState.Stopped, states);
    }

    [Fact]
    public void StateChanged_FiresOnPause()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        var states = new List<PlaybackState>();
        engine.StateChanged += (_, s) => states.Add(s);

        engine.Load("track.mp3");
        engine.Play();
        engine.Pause();

        Assert.Contains(PlaybackState.Paused, states);
    }

    // =========================================================================
    // 7. PlaybackEnded fires when triggered
    // =========================================================================

    [Fact]
    public void PlaybackEnded_CanBeTriggeredByFake()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        var fired = false;
        engine.PlaybackEnded += (_, _) => fired = true;

        engine.Load("track.mp3");
        engine.Play();
        engine.SimulateTrackEnd();

        Assert.True(fired);
        Assert.Equal(PlaybackState.Stopped, engine.State);
    }

    // =========================================================================
    // 8. Volume propagates
    // =========================================================================

    [Fact]
    public void SettingVolume_IsReflectedBack()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0, Volume = 0.5 };
        Assert.Equal(0.5, engine.Volume);

        engine.Volume = 0.75;
        Assert.Equal(0.75, engine.Volume);
    }

    // =========================================================================
    // 9. GetOutputDevices returns stub list
    // =========================================================================

    [Fact]
    public void GetOutputDevices_ReturnsConfiguredStubs()
    {
        var engine = new FakePreListenEngine
        {
            OutputDevice = 0,
            StubbedDevices =
            [
                new AudioOutputDevice(0, "Headphones (USB)", false),
                new AudioOutputDevice(1, "Speakers", true),
            ]
        };

        var devices = engine.GetOutputDevices();
        Assert.Equal(2, devices.Count);
        Assert.Equal("Headphones (USB)", devices[0].Name);
    }

    // =========================================================================
    // 10. Dispose does not throw
    // =========================================================================

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");
        engine.Play();
        engine.Dispose(); // must not throw
        Assert.True(engine.IsDisposed);
    }
}

// =============================================================================
// Fake IPreListenEngine — in-process stub, no BASS dependency
// =============================================================================

/// <summary>
/// Minimal in-process fake for <see cref="IPreListenEngine"/>.
///
/// Simulates the documented state machine:
/// - OutputDevice == -1 → Load/Play are no-ops.
/// - Load sets a non-zero Duration and initialises State = Stopped.
/// - Play  → Playing  (fires StateChanged).
/// - Pause → Paused   (fires StateChanged, retains Position).
/// - Stop  → Stopped  + Position = 0 (fires StateChanged).
/// - Unload → Stopped + Duration = 0 (fires StateChanged).
/// - SimulateTrackEnd → Stopped + fires both StateChanged and PlaybackEnded.
/// </summary>
file sealed class FakePreListenEngine : IPreListenEngine
{
    private PlaybackState _state = PlaybackState.Stopped;
    private double _volume = 1.0;
    private bool _loaded;

    public int  LoadCount   { get; private set; }
    public bool IsDisposed  { get; private set; }

    public List<AudioOutputDevice> StubbedDevices { get; set; } = [];

    // IPreListenEngine members

    public PlaybackState State    => _state;
    public double        Volume   { get => _volume; set => _volume = value; }
    public TimeSpan      Position { get; set; }
    public TimeSpan      Duration { get; private set; }
    public int           OutputDevice { get; set; } = -1;

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler?                PlaybackEnded;

    public void Load(string filePath)
    {
        if (OutputDevice == -1) return;
        LoadCount++;
        _loaded   = true;
        Duration  = TimeSpan.FromMinutes(5); // stub — non-zero so tests can assert
        Position  = TimeSpan.Zero;
        SetState(PlaybackState.Stopped);
    }

    public void Play()
    {
        if (OutputDevice == -1 || !_loaded) return;
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        if (_state != PlaybackState.Playing) return;
        SetState(PlaybackState.Paused);
    }

    public void Stop()
    {
        Position = TimeSpan.Zero;
        SetState(PlaybackState.Stopped);
    }

    public void Unload()
    {
        _loaded  = false;
        Duration = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        SetState(PlaybackState.Stopped);
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => StubbedDevices;

    public void Dispose() => IsDisposed = true;

    /// <summary>
    /// Test helper: simulate the track reaching its natural end (as BassPreListenEngine would do
    /// from its SYNCPROC). Transitions state to Stopped and fires both StateChanged and PlaybackEnded.
    /// </summary>
    public void SimulateTrackEnd()
    {
        SetState(PlaybackState.Stopped);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(PlaybackState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(this, next);
    }
}
