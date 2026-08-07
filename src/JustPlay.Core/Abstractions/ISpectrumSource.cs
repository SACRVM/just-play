using System;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// A source of live spectrum + limiter telemetry for the SHARED in-app spectrum analyzer
/// (JustPlay.UI's <c>SpectrumWindow</c>). Implemented by each app's audio engine - JUST PLAY's
/// playback <see cref="IAudioEngine"/> today, JUST STREAM's input engine and JUST MASTER later - so
/// the analyzer window is written ONCE and reused across the suite instead of bound to one app's
/// engine type.
/// </summary>
public interface ISpectrumSource
{
    /// <summary>
    /// Sample the current spectrum at TWO taps on the output bus: <paramref name="dryMagnitudes"/> =
    /// BEFORE the DSP rack (EQ / tilt / punch / limiter), <paramref name="wetMagnitudes"/> = AFTER the
    /// whole rack (what is actually heard / streamed). Each span is filled with the SAME number of
    /// log-spaced 1/6-octave band values from 20 Hz to 20 kHz (pass equal-length spans, ideally
    /// <c>60</c> to match the offline <c>SpectralProfile</c>/<c>SpectralTarget</c>). Each value is the
    /// summed linear POWER in that band (the caller converts to dB), so the curve is directly
    /// comparable to a golden target curve. Zeros when nothing is playing or the tap is disabled.
    /// UI-thread safe and cheap - call each render frame.
    /// </summary>
    void GetSpectrum(Span<float> dryMagnitudes, Span<float> wetMagnitudes);

    /// <summary>
    /// Enable / disable the spectrum capture taps. The analyzer window turns them on while open and
    /// off when closed, so the extra mixer DSP costs nothing the rest of the time. Idempotent.
    /// </summary>
    void SetSpectrumTapEnabled(bool enabled);

    /// <summary>
    /// The output limiter's CURRENT gain reduction in dB as a non-negative value (0 = not limiting /
    /// limiter off / nothing playing; e.g. 2.3 = pulling peaks down by 2.3 dB). Drives the analyzer's
    /// "where does it flatten" meter. UI-thread safe and cheap - call each render frame.
    /// </summary>
    double GetLimiterGainReductionDb();

    /// <summary>
    /// Current per-channel OUTPUT peak level (POST-bus - what is actually heard / streamed) as linear
    /// 0..1: <paramref name="leftPeak"/> / <paramref name="rightPeak"/>. Drives the analyzer's vertical
    /// L/R level meter. 0 when nothing is playing. UI-thread safe and cheap - call each render frame.
    /// </summary>
    void GetOutputLevels(out float leftPeak, out float rightPeak);
}
