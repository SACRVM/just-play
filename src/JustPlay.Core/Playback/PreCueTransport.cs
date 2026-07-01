using System.Linq;
using JustPlay.Core.Models;

namespace JustPlay.Core.Playback;

/// <summary>
/// Pure-logic helpers for the headphone pre-cue ("PFL") transport — Pre-Cue v2 (Phase A).
///
/// Both methods here are called from <c>MainWindowViewModel</c> (the ±30s jump commands and the
/// pre-cue-tab device-poll) but live in Core, platform-agnostic, so they're unit-testable from
/// <c>tests/JustPlay.Core.Tests/PreListenEngineTests.cs</c> without Avalonia/BASS — the App project
/// has no test project of its own (see CLAUDE.md "Tests").
/// </summary>
public static class PreCueTransport
{
    /// <summary>
    /// Computes a ±<paramref name="delta"/> jump from <paramref name="current"/>, clamped to
    /// [0, <paramref name="duration"/>]. Backs <c>CueJumpForwardCommand</c>/<c>CueJumpBackCommand</c>
    /// (±30 s) — the VM pushes the result back through the existing <c>PreCuePositionSeconds</c>
    /// setter, reusing <see cref="Abstractions.IPreListenEngine.Position"/>'s seek-hold bookkeeping
    /// exactly as a manual slider drag would.
    /// </summary>
    /// <param name="current">The cue track's current playhead position.</param>
    /// <param name="delta">Signed jump amount (e.g. +30 s forward, −30 s back).</param>
    /// <param name="duration">The loaded track's duration. Zero (nothing loaded) is treated as "no
    /// upper clamp" — <paramref name="current"/> is expected to already be zero in that case.</param>
    public static TimeSpan ClampedJump(TimeSpan current, TimeSpan delta, TimeSpan duration)
    {
        var target = current + delta;
        if (target < TimeSpan.Zero) return TimeSpan.Zero;
        if (duration > TimeSpan.Zero && target > duration) return duration;
        return target;
    }

    /// <summary>
    /// The auto-reconnect "CLOU": decides whether the saved-by-NAME headphone device should be
    /// bound this poll. Called every ~200 ms while the pre-cue tab is open (gated by
    /// <c>IsPreCueTab</c> in <c>MainWindowViewModel.Tick</c>) with a fresh
    /// <see cref="Abstractions.IPreListenEngine.GetOutputDevices"/> enumeration.
    ///
    /// <para>Hard rule (never violate, per <c>roadmap-precue-headphone</c>): only matches by the
    /// SAVED NAME — never falls back to <see cref="AudioOutputDevice.IsDefault"/> — cue audio must
    /// never land on the speakers just because nothing else matched.</para>
    /// </summary>
    /// <param name="currentSelection">The currently-bound headphone device, or null if unbound.</param>
    /// <param name="savedDeviceName">The user's saved headphone device name (persisted independently
    /// of the live selection so it survives a temporary disconnect — see
    /// <c>MainWindowViewModel._savedHeadphoneDeviceName</c>).</param>
    /// <param name="freshDevices">A fresh device enumeration for this poll.</param>
    /// <returns>The device to auto-bind, or null to leave the current selection untouched — either
    /// because something is already bound (never override a live/explicit pick), there is no saved
    /// name, or the saved device still isn't present.</returns>
    public static AudioOutputDevice? TryAutoRebind(
        AudioOutputDevice? currentSelection,
        string? savedDeviceName,
        IReadOnlyList<AudioOutputDevice> freshDevices)
    {
        if (currentSelection is not null) return null;
        if (string.IsNullOrEmpty(savedDeviceName)) return null;
        return freshDevices.FirstOrDefault(d => d.Name == savedDeviceName);
    }
}
