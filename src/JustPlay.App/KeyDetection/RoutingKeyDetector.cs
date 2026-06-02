using System.Threading;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.ML;

namespace JustPlay.App.KeyDetection;

/// <summary>
/// The shipped <see cref="IKeyDetector"/>: prefers the trained "AI key" model
/// (<see cref="MlKeyDetector"/>, MIREX ~0.75) when the user has it enabled AND the model +
/// ONNX runtime actually loaded, otherwise uses the always-available DSP template detector
/// (<see cref="HpcpKeyDetector"/>, ~0.71). Also falls back to DSP if the ML detector returns
/// null for a given track (e.g. silence). This keeps the app robust: no model, no runtime,
/// or the toggle off → it simply runs the lightweight path, never crashes.
/// </summary>
public sealed class RoutingKeyDetector : IKeyDetector
{
    private readonly MlKeyDetector _ml;
    private readonly HpcpKeyDetector _dsp;
    private readonly ISettingsService _settings;

    public RoutingKeyDetector(MlKeyDetector ml, HpcpKeyDetector dsp, ISettingsService settings)
    {
        _ml = ml;
        _dsp = dsp;
        _settings = settings;
    }

    public (MusicalKey Key, double Confidence)? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        if (_settings.Current.UseAiKeyDetection && _ml.IsAvailable)
        {
            var ml = _ml.Detect(audio, ct);
            if (ml is not null)
                return ml;
        }
        return _dsp.Detect(audio, ct);
    }
}
