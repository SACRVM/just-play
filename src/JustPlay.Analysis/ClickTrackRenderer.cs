using JustPlay.Core.Models;

namespace JustPlay.Analysis;

/// <summary>
/// Renders the audible click-check for a <see cref="BeatMap"/>: the original audio with
/// a metronome pip on every tracked beat.
///
/// <para>This is POC-0 of the beatbed feature (the live-remix beat carrier) — the
/// falsification test for <see cref="BeatMapTracker"/> on real recordings: play the
/// render; if the pips stay ON the drummer for the whole track (including breakdowns),
/// the beat map is trustworthy enough to render a carrier onto.</para>
///
/// <para>Two pip pitches make the tracker's honesty audible: a HIGH pip marks a beat
/// with real onset support, a LOW pip marks a beat the tracker interpolated (bridged
/// breakdown) — so the ear test also reveals WHERE the tracker was guessing.</para>
/// </summary>
public static class ClickTrackRenderer
{
    private const double SupportedPipHz = 1500.0;  // beat with a real detected hit
    private const double BridgedPipHz   = 750.0;   // interpolated beat (breakdown bridge)

    /// <summary>BeatStrength below this renders as the LOW "bridged" pip.</summary>
    private const float BridgedStrengthMax = 0.25f;

    private const double PipSeconds  = 0.03;   // pip length
    private const double PipDecayTau = 0.006;  // exponential decay time constant

    // Original ducked slightly so the pips always cut through; pips well under
    // full scale so original + pip cannot exceed the WavWriter clamp audibly.
    private const float OriginalGain = 0.75f;
    private const float PipGain      = 0.55f;

    /// <summary>
    /// Mixes the original samples with a pip at every beat of <paramref name="map"/>.
    /// Returns a new buffer of the same length; the input is not modified.
    /// </summary>
    public static float[] RenderMix(float[] original, int sampleRate, BeatMap map)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var mix = new float[original.Length];
        for (var i = 0; i < original.Length; i++)
            mix[i] = original[i] * OriginalGain;

        var pipLen = (int)(PipSeconds * sampleRate);
        for (var k = 0; k < map.Count; k++)
        {
            var hz    = map.BeatStrengths[k] >= BridgedStrengthMax ? SupportedPipHz : BridgedPipHz;
            var start = (int)Math.Round(map.BeatTimes[k] * sampleRate);
            for (var i = 0; i < pipLen; i++)
            {
                var idx = start + i;
                if (idx < 0 || idx >= mix.Length) continue;
                var t = i / (double)sampleRate;
                mix[idx] += (float)(PipGain * Math.Exp(-t / PipDecayTau) * Math.Sin(2.0 * Math.PI * hz * t));
            }
        }
        return mix;
    }
}
