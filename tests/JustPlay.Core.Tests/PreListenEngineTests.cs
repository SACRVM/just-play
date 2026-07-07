using System;
using System.Collections.Generic;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Playback;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Unit tests for the IPreListenEngine state machine contract, plus the Pre-Cue v2 (Phase A)
/// pure-logic helpers in <see cref="PreCueTransport"/> — both exercised against
/// <see cref="FakePreListenEngine"/> (no BASS, no Avalonia; the App project has no test project of
/// its own, so this is where the shared VM-facing logic gets covered — see CLAUDE.md "Tests").
///
/// Coverage:
/// 1. OutputDevice == -1 means disabled: Load/Play are silent no-ops.
/// 2. Load → Play → state == Playing.
/// 3. Play → Stop → state == Stopped (position reset). (Pause was removed in v2 — no pause state.)
/// 4. Unload releases the track (Duration == 0 after Unload).
/// 5. StateChanged event fires on transitions.
/// 6. PlaybackEnded event fires when the fake triggers it.
/// 7. Volume setter propagates to the engine.
/// 8. GetOutputDevices returns the configured stub list.
/// 9. Dispose does not throw.
/// 10. PreCueTransport.ClampedJump — the ±30s jump math (CueJumpForwardCommand/CueJumpBackCommand).
/// 11. Single-slot replace-on-load — Load() fully replaces the previous cue track, never stacks.
/// 12. PreCueTransport.TryAutoRebind — the auto-reconnect "CLOU" (saved-by-name device reappearing).
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
    // 3. Play → Pause → Stop transitions
    //    (v2 dropped Pause; N26 P1.1 brought it back for the PRE CUE FINDER's play/pause
    //    browsing mode — Pause keeps the position, Play resumes, Stop rewinds.)
    // =========================================================================

    [Fact]
    public void Pause_KeepsPosition_AndPlayResumes()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");
        engine.Play();
        engine.Position = TimeSpan.FromSeconds(42);
        engine.Pause();

        Assert.Equal(PlaybackState.Paused, engine.State);
        Assert.Equal(TimeSpan.FromSeconds(42), engine.Position);

        engine.Play();
        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.Equal(TimeSpan.FromSeconds(42), engine.Position);
    }

    [Fact]
    public void Pause_WhenNotPlaying_IsNoOp()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("track.mp3");
        engine.Pause(); // never played — must stay Stopped, not flip to Paused

        Assert.Equal(PlaybackState.Stopped, engine.State);
    }

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
    // 4. Unload releases track → Duration = 0
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
    // 5. StateChanged fires on each transition
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

    // =========================================================================
    // 6. PlaybackEnded fires when triggered
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
    // 7. Volume propagates
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
    // 8. GetOutputDevices returns stub list
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
    // 9. Dispose does not throw
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

    // =========================================================================
    // 10. PreCueTransport.ClampedJump — ±30s jump math (CueJumpForwardCommand/CueJumpBackCommand)
    // =========================================================================

    [Fact]
    public void ClampedJump_ForwardWithinRange_AddsDelta()
    {
        var result = PreCueTransport.ClampedJump(
            current: TimeSpan.FromSeconds(60),
            delta: TimeSpan.FromSeconds(30),
            duration: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(90), result);
    }

    [Fact]
    public void ClampedJump_BackWithinRange_SubtractsDelta()
    {
        var result = PreCueTransport.ClampedJump(
            current: TimeSpan.FromSeconds(60),
            delta: TimeSpan.FromSeconds(-30),
            duration: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(30), result);
    }

    [Fact]
    public void ClampedJump_PastTheEnd_ClampsToDuration()
    {
        // 4:50 + 30s would overshoot a 5:00 track — must clamp to the duration, not overshoot.
        var result = PreCueTransport.ClampedJump(
            current: TimeSpan.FromSeconds(290),
            delta: TimeSpan.FromSeconds(30),
            duration: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact]
    public void ClampedJump_BeforeTheStart_ClampsToZero()
    {
        // 10s back from :15 would go negative — must clamp to zero, not go negative.
        var result = PreCueTransport.ClampedJump(
            current: TimeSpan.FromSeconds(15),
            delta: TimeSpan.FromSeconds(-30),
            duration: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void ClampedJump_NothingLoaded_ZeroDurationSkipsUpperClamp()
    {
        // Duration == 0 (nothing loaded) is treated as "no upper clamp" — in practice the VM guards
        // the jump commands behind HasPreCueCurrent (CanExecute) so this never fires from the UI with
        // a nonzero current, but the pure function's contract is documented here.
        var result = PreCueTransport.ClampedJump(
            current: TimeSpan.Zero,
            delta: TimeSpan.FromSeconds(30),
            duration: TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(30), result);
    }

    // =========================================================================
    // 11. Single-slot replace-on-load — Load() fully replaces, never stacks (Pre-Cue v2: ONE slot,
    //     not an audition list; loading track B while A is cued REPLACES A, exactly what
    //     MainWindowViewModel.LoadPreCueTrackAsync relies on).
    // =========================================================================

    [Fact]
    public void Load_Twice_ReplacesThePreviousTrack_DoesNotStack()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };

        engine.Load("A.mp3");
        Assert.Equal("A.mp3", engine.LastLoadedPath);
        Assert.Equal(1, engine.LoadCount);

        engine.Load("B.mp3");
        Assert.Equal("B.mp3", engine.LastLoadedPath);
        Assert.Equal(2, engine.LoadCount); // a second, independent load — not appended to a list
        Assert.True(engine.Duration > TimeSpan.Zero); // the ONE slot is still playable, freshly loaded
    }

    [Fact]
    public void Load_WhilePlaying_ReplacesAndResetsPosition()
    {
        var engine = new FakePreListenEngine { OutputDevice = 0 };
        engine.Load("A.mp3");
        engine.Play();
        engine.Position = TimeSpan.FromSeconds(42);

        engine.Load("B.mp3"); // Chloe scouts a new track mid-audition — the slot replaces instantly

        Assert.Equal("B.mp3", engine.LastLoadedPath);
        Assert.Equal(TimeSpan.Zero, engine.Position); // fresh load starts at the top
    }

    // =========================================================================
    // 12. PreCueTransport.TryAutoRebind — the auto-reconnect "CLOU"
    // =========================================================================

    [Fact]
    public void TryAutoRebind_SavedDeviceAbsent_ReturnsNull()
    {
        var engine = new FakePreListenEngine
        {
            StubbedDevices = [new AudioOutputDevice(1, "Speakers", true)]
        };

        var pick = PreCueTransport.TryAutoRebind(
            currentSelection: null,
            savedDeviceName: "My Headphones (Bluetooth)",
            freshDevices: engine.GetOutputDevices());

        Assert.Null(pick);
    }

    [Fact]
    public void TryAutoRebind_SavedDeviceReappears_SelectsItByName()
    {
        var engine = new FakePreListenEngine
        {
            StubbedDevices =
            [
                new AudioOutputDevice(1, "Speakers", true),
                new AudioOutputDevice(2, "My Headphones (Bluetooth)", false),
            ]
        };

        var pick = PreCueTransport.TryAutoRebind(
            currentSelection: null,
            savedDeviceName: "My Headphones (Bluetooth)",
            freshDevices: engine.GetOutputDevices());

        Assert.NotNull(pick);
        Assert.Equal("My Headphones (Bluetooth)", pick!.Name);
    }

    [Fact]
    public void TryAutoRebind_NeverPicksDefault_WhenNoSavedNameMatches()
    {
        // Hard rule: cue audio must never land on the speakers. Even though "Speakers" is
        // IsDefault=true and present, it must NOT be auto-selected when the saved name doesn't match.
        var engine = new FakePreListenEngine
        {
            StubbedDevices = [new AudioOutputDevice(1, "Speakers", true)]
        };

        var pick = PreCueTransport.TryAutoRebind(
            currentSelection: null,
            savedDeviceName: "My Headphones (Bluetooth)",
            freshDevices: engine.GetOutputDevices());

        Assert.Null(pick);
    }

    [Fact]
    public void TryAutoRebind_NoSavedName_ReturnsNull()
    {
        var engine = new FakePreListenEngine
        {
            StubbedDevices = [new AudioOutputDevice(1, "Headphones", false)]
        };

        var pick = PreCueTransport.TryAutoRebind(
            currentSelection: null,
            savedDeviceName: null,
            freshDevices: engine.GetOutputDevices());

        Assert.Null(pick);
    }

    [Fact]
    public void TryAutoRebind_AlreadyBound_NeverOverridesLiveSelection()
    {
        // Never overrides an explicit/already-bound device — the poll only fills in an EMPTY slot.
        var engine = new FakePreListenEngine
        {
            StubbedDevices =
            [
                new AudioOutputDevice(1, "Other Headphones", false),
                new AudioOutputDevice(2, "My Headphones (Bluetooth)", false),
            ]
        };
        var currentlyBound = new AudioOutputDevice(1, "Other Headphones", false);

        var pick = PreCueTransport.TryAutoRebind(
            currentSelection: currentlyBound,
            savedDeviceName: "My Headphones (Bluetooth)",
            freshDevices: engine.GetOutputDevices());

        Assert.Null(pick);
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
/// - Load sets a non-zero Duration, resets Position, initialises State = Stopped, and RECORDS the
///   path in <see cref="LastLoadedPath"/> — a second Load() call replaces it (single-slot semantics,
///   never stacks), mirroring BassPreListenEngine.Load's FreeSource-then-load-new behaviour.
/// - Play  → Playing  (fires StateChanged).
/// - Stop  → Stopped  + Position = 0 (fires StateChanged). (No Pause in v2.)
/// - Unload → Stopped + Duration = 0 (fires StateChanged).
/// - SimulateTrackEnd → Stopped + fires both StateChanged and PlaybackEnded.
/// </summary>
file sealed class FakePreListenEngine : IPreListenEngine
{
    private PlaybackState _state = PlaybackState.Stopped;
    private double _volume = 1.0;
    private bool _loaded;

    public int     LoadCount      { get; private set; }
    public bool    IsDisposed     { get; private set; }
    public string? LastLoadedPath { get; private set; }

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
        LastLoadedPath = filePath; // replaces whatever was here before — single slot, not a list
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
        SetState(PlaybackState.Paused); // position kept — Play() resumes
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
