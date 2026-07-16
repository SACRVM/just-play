using System;
using JustPlay.Core.Playback;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Unit tests for <see cref="CueArbiter"/> — the N26 "cue wins on a shared device" decision
/// logic. No BASS, no Avalonia; pure state machine (see the class doc for why it only ever
/// emits a volume-level Suppress/Restore action, never a play/pause command).
///
/// Coverage:
/// 1. ShouldSuppress (pure gate) — same device + playing, different device, not playing,
///    unresolved (-1) device on either side.
/// 2. Evaluate — suppress on same-device cue-start.
/// 3. Evaluate — restore on cue-stop.
/// 4. Evaluate — permanent no-op on different devices, even while "playing".
/// 5. Evaluate — double-start (repeated Suppress-triggering calls) never re-fires / re-captures.
/// 6. Evaluate — repeated Restore-triggering calls after already restored never re-fire.
/// 7. Evaluate — device changes mid-cue (same → different → restores; different → same → suppresses).
/// 8. IsSuppressed reflects the last emitted action.
/// 9. "Paused-main restore" — CueArbiterAction has no play/pause member, so restoring can never
///    force main playback; the type system rules it out.
/// </summary>
public class CueArbiterTests
{
    // =========================================================================
    // 1. ShouldSuppress — the pure gate
    // =========================================================================

    [Fact]
    public void ShouldSuppress_SameDeviceAndPlaying_ReturnsTrue()
    {
        Assert.True(CueArbiter.ShouldSuppress(cueIsPlaying: true, mainDeviceIndex: 2, cueDeviceIndex: 2));
    }

    [Fact]
    public void ShouldSuppress_DifferentDevice_ReturnsFalse()
    {
        Assert.False(CueArbiter.ShouldSuppress(cueIsPlaying: true, mainDeviceIndex: 2, cueDeviceIndex: 3));
    }

    [Fact]
    public void ShouldSuppress_NotPlaying_ReturnsFalse_EvenOnSameDevice()
    {
        // Paused/stopped cue counts as "not playing" — the cue only wins while actually audible.
        Assert.False(CueArbiter.ShouldSuppress(cueIsPlaying: false, mainDeviceIndex: 2, cueDeviceIndex: 2));
    }

    [Fact]
    public void ShouldSuppress_UnresolvedMainDevice_ReturnsFalse()
    {
        // -1 = "not yet resolved" on either side must never accidentally compare equal.
        Assert.False(CueArbiter.ShouldSuppress(cueIsPlaying: true, mainDeviceIndex: -1, cueDeviceIndex: -1));
    }

    [Fact]
    public void ShouldSuppress_UnresolvedCueDevice_ReturnsFalse()
    {
        Assert.False(CueArbiter.ShouldSuppress(cueIsPlaying: true, mainDeviceIndex: 2, cueDeviceIndex: -1));
    }

    // =========================================================================
    // 2. Evaluate — suppress on same-device cue-start
    // =========================================================================

    [Fact]
    public void Evaluate_CueStartsOnSameDevice_EmitsSuppress()
    {
        var arbiter = new CueArbiter();

        var action = arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4);

        Assert.Equal(CueArbiterAction.Suppress, action);
        Assert.True(arbiter.IsSuppressed);
    }

    // =========================================================================
    // 3. Evaluate — restore on cue-stop
    // =========================================================================

    [Fact]
    public void Evaluate_CueStopsAfterSuppress_EmitsRestore()
    {
        var arbiter = new CueArbiter();
        arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4); // suppress

        var action = arbiter.Evaluate(cueIsPlaying: false, mainDeviceIndex: 4, cueDeviceIndex: 4); // stop

        Assert.Equal(CueArbiterAction.Restore, action);
        Assert.False(arbiter.IsSuppressed);
    }

    // =========================================================================
    // 4. Evaluate — different devices: permanent no-op
    // =========================================================================

    [Fact]
    public void Evaluate_DifferentDevices_NeverSuppresses_AcrossFullPlayStopCycle()
    {
        var arbiter = new CueArbiter();

        Assert.Null(arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 1, cueDeviceIndex: 2));
        Assert.False(arbiter.IsSuppressed);

        Assert.Null(arbiter.Evaluate(cueIsPlaying: false, mainDeviceIndex: 1, cueDeviceIndex: 2));
        Assert.False(arbiter.IsSuppressed);
    }

    // =========================================================================
    // 5. Evaluate — double-start: repeated "still playing, still same device" never re-fires
    // =========================================================================

    [Fact]
    public void Evaluate_DoubleStart_SecondCallIsNoOp_DoesNotReCaptureDuckedLevel()
    {
        // Simulates a debounced double Play() (finder's 1s-debounce mode) racing two evaluations
        // through with identical inputs — the second must be a pure no-op, not a second Suppress
        // (which, at the adapter level, would risk "capturing" the already-ducked 0 as the level
        // to restore to later).
        var arbiter = new CueArbiter();

        var first = arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4);
        var second = arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4);

        Assert.Equal(CueArbiterAction.Suppress, first);
        Assert.Null(second);
        Assert.True(arbiter.IsSuppressed);
    }

    // =========================================================================
    // 6. Evaluate — repeated restore-triggering calls after already restored never re-fire
    // =========================================================================

    [Fact]
    public void Evaluate_RepeatedStopAfterRestore_NeverReFiresRestore()
    {
        var arbiter = new CueArbiter();
        arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4);   // suppress
        arbiter.Evaluate(cueIsPlaying: false, mainDeviceIndex: 4, cueDeviceIndex: 4);  // restore

        var again = arbiter.Evaluate(cueIsPlaying: false, mainDeviceIndex: 4, cueDeviceIndex: 4);

        Assert.Null(again);
        Assert.False(arbiter.IsSuppressed);
    }

    // =========================================================================
    // 7. Evaluate — device changes mid-cue
    // =========================================================================

    [Fact]
    public void Evaluate_MainDeviceChangesAwayMidCue_EmitsRestore()
    {
        var arbiter = new CueArbiter();
        arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4); // suppress (same device)

        // Main engine's output moves to a different device while the cue keeps playing.
        var action = arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 7, cueDeviceIndex: 4);

        Assert.Equal(CueArbiterAction.Restore, action);
        Assert.False(arbiter.IsSuppressed);
    }

    [Fact]
    public void Evaluate_MainDeviceChangesIntoMatchMidCue_EmitsSuppress()
    {
        var arbiter = new CueArbiter();
        arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 7, cueDeviceIndex: 4); // different devices, no-op

        // Main engine's output moves onto the cue's device while the cue keeps playing.
        var action = arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4);

        Assert.Equal(CueArbiterAction.Suppress, action);
        Assert.True(arbiter.IsSuppressed);
    }

    [Fact]
    public void Evaluate_CueDeviceChangesAwayMidSuppression_EmitsRestore()
    {
        var arbiter = new CueArbiter();
        arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4); // suppress

        // Headphone selection changes to a different device while cue is still playing.
        var action = arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 9);

        Assert.Equal(CueArbiterAction.Restore, action);
        Assert.False(arbiter.IsSuppressed);
    }

    // =========================================================================
    // 8. IsSuppressed reflects state
    // =========================================================================

    [Fact]
    public void IsSuppressed_DefaultsToFalse()
    {
        var arbiter = new CueArbiter();
        Assert.False(arbiter.IsSuppressed);
    }

    // =========================================================================
    // 9. "Paused-main restore" — restoring can never force playback, by construction
    // =========================================================================

    [Fact]
    public void CueArbiterAction_HasOnlyDuckingMembers_CanNeverExpressForcePlay()
    {
        // The arbiter has no notion of "was main playing" at all — it only ever emits a
        // device-output VOLUME instruction (Suppress = duck to 0, Restore = back to the stored
        // level). BassAudioEngine.SetDucked never calls Play()/Pause() for either action, so a
        // main engine that was paused when the cue started is still paused after the cue stops —
        // there is no "resume" action for it to accidentally trigger. Asserting the action
        // vocabulary is exactly {Suppress, Restore} makes that a compile-time-checkable invariant,
        // not just a convention.
        var members = Enum.GetValues<CueArbiterAction>();

        Assert.Equal(2, members.Length);
        Assert.Contains(CueArbiterAction.Suppress, members);
        Assert.Contains(CueArbiterAction.Restore, members);
    }

    [Fact]
    public void Evaluate_SuppressThenRestore_WorksIdenticallyRegardlessOfMainPlayState()
    {
        // The arbiter's decision never depends on whether the main engine happens to be playing
        // or paused — that input doesn't exist in its signature. This test simply documents that
        // the same (cueIsPlaying, mainDevice, cueDevice) sequence always produces the same
        // Suppress → Restore pair; whatever BassAudioEngine.SetDucked does with "restore" (slide
        // BASS_ATTRIB_VOL back to the stored _volume) never touches transport state either way.
        var arbiter = new CueArbiter();

        var onCueStart = arbiter.Evaluate(cueIsPlaying: true, mainDeviceIndex: 4, cueDeviceIndex: 4);
        var onCueStop  = arbiter.Evaluate(cueIsPlaying: false, mainDeviceIndex: 4, cueDeviceIndex: 4);

        Assert.Equal(CueArbiterAction.Suppress, onCueStart);
        Assert.Equal(CueArbiterAction.Restore, onCueStop);
    }
}
