using System.Threading;
using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;

namespace JustPlay.ML;

/// <summary>
/// The single, canonical key detector for the WHOLE product — used identically by the app
/// (DI) AND the headless CLI (EngineComposer). It always uses the BEST available method:
/// the trained ML model (<see cref="MlKeyDetector"/>, MIREX ~0.75) when the model + ONNX
/// runtime loaded, else the always-available DSP template (<see cref="HpcpKeyDetector"/>,
/// ~0.71). Per-track fallback to DSP if the model returns null (e.g. silence).
///
/// <para>There is deliberately NO user toggle: key detection always uses the best method.
/// The earlier split — CLI on DSP, app on ML — meant a track tagged by the console showed a
/// DIFFERENT key in the UI (the key-conflict-dot root cause), which made the CLI's "truth"
/// inconsistent with the app. This type guarantees both paths agree by construction.</para>
///
/// <para>Lives in JustPlay.ML (the ONNX adapter) because it depends on <see cref="MlKeyDetector"/>;
/// JustPlay.ML already references JustPlay.Analysis, so it can wrap the DSP detector too. Loading
/// is graceful: if the model/runtime is absent, <see cref="IsMlActive"/> is false and it runs the
/// DSP path — never crashes.</para>
/// </summary>
public sealed class BestKeyDetector : IKeyDetector, IDisposable
{
    private readonly MlKeyDetector _ml;
    private readonly HpcpKeyDetector _dsp;

    /// <summary>DI ctor — App passes the shared singletons.</summary>
    public BestKeyDetector(MlKeyDetector ml, HpcpKeyDetector dsp)
    {
        _ml = ml;
        _dsp = dsp;
    }

    /// <summary>Convenience ctor for headless composition (CLI/MCP) without DI.</summary>
    public BestKeyDetector() : this(new MlKeyDetector(), new HpcpKeyDetector()) { }

    /// <summary>True when the ML model + ONNX runtime loaded, so detection uses it (else DSP).</summary>
    public bool IsMlActive => _ml.IsAvailable;

    public (MusicalKey Key, double Confidence)? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        if (_ml.IsAvailable)
        {
            var ml = _ml.Detect(audio, ct);
            if (ml is not null)
                return ml;
        }
        return _dsp.Detect(audio, ct);
    }

    public void Dispose() => _ml.Dispose();
}
