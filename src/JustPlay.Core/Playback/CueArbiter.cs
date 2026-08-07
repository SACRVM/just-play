namespace JustPlay.Core.Playback;

/// <summary>
/// The action a <see cref="CueArbiter"/> wants performed. Deliberately only two members: this
/// feature NEVER forces play/pause on the main engine (see <see cref="CueArbiter"/> class doc) -
/// the type itself makes that impossible to get wrong later.
/// </summary>
public enum CueArbiterAction
{
    /// <summary>Duck the main engine's audible (device) output to silence - the cue wins.</summary>
    Suppress,

    /// <summary>Restore the main engine's audible output to its prior level exactly.</summary>
    Restore,
}

/// <summary>
/// Pure decision logic for the N26 "cue wins" invariant: when the PRE-CUE (headphone audition)
/// output device is the SAME device as the main playback output, the two must never sound at
/// once - starting the cue suppresses the main engine's audible output on that device; stopping
/// the cue (or the cue leaving its audible/Playing state) restores it. Different devices are a
/// permanent no-op.
///
/// <para><b>Why this only ever touches VOLUME, never Play/Pause:</b> the Icecast/broadcast
/// encoder taps the main engine's mixer via BASS's own DSP chain (see
/// <c>BassAudioEngine.OutputChannel</c> / <c>BassBroadcastService</c>). Pausing the main engine's
/// source would also silence that DSP tap - i.e. it would silence the STREAM, which is the one
/// thing this feature must never do (cueing is monitor-only). The adapter
/// (<c>BassAudioEngine.SetDucked</c>) instead rides BASS_ATTRIB_VOL, which BASS's own docs state
/// "is not present in the sample data returned by BASS_ChannelGetData" - i.e. it only affects the
/// channel's audible/device output, never anything a DSP (the encoder included) reads. Because
/// suppression never touches play/pause state, "cue started while main is paused" is handled for
/// free: restore reapplies the prior volume and the transport state is simply never disturbed -
/// there is no play state to get wrong.</para>
///
/// <para>Pure, stateless-enough-to-unit-test, no BASS/Avalonia. Feed <see cref="Evaluate"/>
/// whenever any of the three inputs might have changed (the cue's play state, either device) and
/// it tells you what to DO - or null when nothing changed since the last call (idempotent, so
/// repeated/rapid re-evaluation with unchanged inputs - e.g. a debounced double Play() - never
/// double-applies or re-captures an already-ducked level).</para>
/// </summary>
public sealed class CueArbiter
{
    private bool _suppressed;

    /// <summary>True while this arbiter's last emitted action was <see cref="CueArbiterAction.Suppress"/>
    /// with no matching <see cref="CueArbiterAction.Restore"/> since.</summary>
    public bool IsSuppressed => _suppressed;

    /// <summary>
    /// Pure gate: should the main engine be suppressed right now? True only when the cue is
    /// actually audible (<paramref name="cueIsPlaying"/>) AND both devices are resolved
    /// (&gt;= 0 - a -1/unconfigured device never matches) AND they are the same index.
    /// </summary>
    public static bool ShouldSuppress(bool cueIsPlaying, int mainDeviceIndex, int cueDeviceIndex)
        => cueIsPlaying
           && mainDeviceIndex >= 0
           && cueDeviceIndex >= 0
           && mainDeviceIndex == cueDeviceIndex;

    /// <summary>
    /// Re-evaluate the gate given the current world state.
    /// </summary>
    /// <param name="cueIsPlaying">True iff the pre-listen engine's State == Playing. Paused and
    /// Stopped both count as "not playing" - the cue only "wins" while actually audible, so
    /// pausing the browse/audition mode releases the main engine again.</param>
    /// <param name="mainDeviceIndex">The main engine's current BASS output device index, or -1 if
    /// not yet resolved (treated as "never matches").</param>
    /// <param name="cueDeviceIndex">The pre-listen engine's current BASS output device index, or
    /// -1 if unconfigured (treated as "never matches").</param>
    /// <returns><see cref="CueArbiterAction.Suppress"/> or <see cref="CueArbiterAction.Restore"/>
    /// the first time the gate flips; <see langword="null"/> when the gate's value is unchanged
    /// from the last call (nothing to do).</returns>
    public CueArbiterAction? Evaluate(bool cueIsPlaying, int mainDeviceIndex, int cueDeviceIndex)
    {
        var shouldSuppress = ShouldSuppress(cueIsPlaying, mainDeviceIndex, cueDeviceIndex);
        if (shouldSuppress == _suppressed) return null;

        _suppressed = shouldSuppress;
        return shouldSuppress ? CueArbiterAction.Suppress : CueArbiterAction.Restore;
    }
}
