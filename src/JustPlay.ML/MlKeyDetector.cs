using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace JustPlay.ML;

/// <summary>
/// "AI key" detector: runs a small trained MLP (<c>keymodel.onnx</c>) on JustPlay's own
/// 36-bin HPCP chroma (<see cref="HpcpKeyDetector.BuildFine36"/>). On the GiantSteps
/// ground-truth set this reaches <b>MIREX ~0.75 / ~69% exact</b> vs the DSP template's
/// 0.712 — it resolves the relative/fifth confusions the template can't.
///
/// <para>The model is tiny (~58 KB, an MLP not a CNN). The only heavyweight piece is the
/// ONNX Runtime native library, which is why this lives in its own adapter project (native
/// dep, like ManagedBass) rather than in the platform-agnostic Core/Analysis layers.</para>
///
/// <para>Loading is graceful: if the model file or the native runtime is missing, the
/// session simply fails to construct, <see cref="IsAvailable"/> stays false, and the
/// caller (the routing detector) falls back to <see cref="HpcpKeyDetector"/>. So the app
/// never crashes for lack of the ML bits.</para>
///
/// Input tensor:  "chroma36"  float[1,36]  (L1-normalised fine chroma).
/// Output tensor: "logits"    float[1,24]  (0-11 major, 12-23 minor); key = argmax,
///                                          confidence = softmax max.
/// </summary>
public sealed class MlKeyDetector : IKeyDetector, IDisposable
{
    private readonly HpcpKeyDetector _feature = new();
    private readonly InferenceSession? _session;
    private readonly string? _inputName;

    public MlKeyDetector()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "keymodel.onnx");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[MlKeyDetector] model not found at {path} — falling back to DSP.");
                return;
            }
            _session = new InferenceSession(path);
            _inputName = _session.InputMetadata.Keys.First();
        }
        catch (Exception ex)
        {
            // Missing native runtime, bad model, etc. Stay unavailable; the router falls back.
            Console.WriteLine($"[MlKeyDetector] unavailable ({ex.GetType().Name}: {ex.Message}) — falling back to DSP.");
            _session = null;
        }
    }

    /// <summary>True when the model + native runtime loaded and the detector can run.</summary>
    public bool IsAvailable => _session is not null;

    public (MusicalKey Key, double Confidence)? Detect(DecodedAudio audio, CancellationToken ct = default)
    {
        if (_session is null || _inputName is null)
            return null;

        var chroma = _feature.BuildFine36(audio, ct);
        if (chroma is null)
            return null;

        var input = new DenseTensor<float>(new[] { 1, chroma.Length });
        for (var i = 0; i < chroma.Length; i++)
            input[0, i] = (float)chroma[i];

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var results = _session.Run(inputs);
        var logits = results.First().AsEnumerable<float>().ToArray();
        if (logits.Length < 24)
            return null;

        // argmax + softmax-max confidence.
        var best = 0;
        for (var i = 1; i < 24; i++)
            if (logits[i] > logits[best]) best = i;

        var maxLogit = logits[best];
        var sum = 0.0;
        for (var i = 0; i < 24; i++) sum += Math.Exp(logits[i] - maxLogit);
        var confidence = 1.0 / sum;   // exp(0)/sum = softmax of the max class

        var pitchClass = best % 12;
        var mode = best < 12 ? KeyMode.Major : KeyMode.Minor;
        return (new MusicalKey(pitchClass, mode), confidence);
    }

    public void Dispose() => _session?.Dispose();
}
