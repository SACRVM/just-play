namespace JustPlay.Core.Models;

/// <summary>
/// Persisted user preferences. Every field is nullable / defaultable so the
/// settings file forward-compatibly survives new fields being added — a
/// settings.json written by today's build, then loaded by tomorrow's build
/// with a new property, just gets the default for that property.
///
/// JustPlay's "stateless DJ player" promise applies to the TRACK QUEUE
/// (close and the queue is empty), not to UI preferences. Theme choice and
/// the tweak toggles are persisted because expecting the user to re-pick
/// "Sunset" every time they open the app would be hostile.
/// </summary>
public sealed record UserSettings
{
    /// <summary>The <see cref="Theming.Theme.Name"/> currently active.</summary>
    public string Theme { get; init; } = "Aurora";

    /// <summary>Whether the vinyl spins during playback (Tweaks toggle).</summary>
    public bool VinylSpinEnabled { get; init; } = true;

    /// <summary>Whether the FFT-driven waveform header animates (Tweaks toggle).</summary>
    public bool WaveformEnabled { get; init; } = true;

    /// <summary>Which queue tab opens on launch ("Up Next" / "Lyrics" / ...).</summary>
    public string DefaultTab { get; init; } = "Up Next";

    public static readonly UserSettings Defaults = new();
}
