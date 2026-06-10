using JustPlay.Analysis;
using JustPlay.Audio.Bass;
using JustPlay.Core.Abstractions;
using JustPlay.Engine;
using JustPlay.Metadata;

namespace JustPlay.Cli;

/// <summary>
/// Composes the JustPlay analysis stack for headless (CLI / MCP) use without DI.
///
/// <para>
/// Mirrors the wiring in <c>JustPlay.App.Program.ConfigureServices</c> but skips
/// Avalonia, ISettingsService, and all UI-only services.  The HPCP key detector is
/// used directly (no ML fallback — MlKeyDetector requires the ONNX runtime DLL which
/// may not be present in a headless environment).
/// </para>
///
/// <para>
/// Call <see cref="Build"/> once per process; reuse the returned components throughout.
/// They are stateless (or safe for concurrent independent file paths) and hold no
/// pooled native resources that need disposal other than the BASS engine itself.
/// </para>
/// </summary>
internal sealed class EngineComposer : IDisposable
{
    private readonly BassAudioDecoder _decoder;
    private readonly BassBpmDetector  _bpmDetector;

    public ITrackAnalysisService AnalysisService { get; }
    public ITrackEngine          Engine          { get; }
    public IMetadataReader       MetadataReader  { get; }

    private EngineComposer(
        BassAudioDecoder decoder,
        BassBpmDetector bpmDetector,
        ITrackAnalysisService analysisService,
        ITrackEngine engine,
        IMetadataReader metadataReader)
    {
        _decoder         = decoder;
        _bpmDetector     = bpmDetector;
        AnalysisService  = analysisService;
        Engine           = engine;
        MetadataReader   = metadataReader;
    }

    /// <summary>
    /// Constructs and returns a composed analysis stack ready for use.
    /// Initialises the BASS library (no output device — decode-only mode via BassAudioDecoder).
    /// </summary>
    public static EngineComposer Build()
    {
        // Bass.Init(-1) is the "no-device / decode-only" mode. BassAudioDecoder and
        // BassBpmDetector use ManagedBass.Bass.CreateStream with BASS_STREAM_DECODE so they
        // never need an output device. Init with device = -1 is the documented way to
        // initialize BASS for pure decoding without audio output. However, BassBpmDetector's
        // internal BASS_FX call may need even device 0. We call with 0 and catch Already.
        // See BassAudioEngine constructor for precedent.
        if (!ManagedBass.Bass.Init())
        {
            var err = ManagedBass.Bass.LastError;
            if (err != ManagedBass.Errors.Already)
                throw new InvalidOperationException($"BASS init failed: {err}");
        }

        var decoder     = new BassAudioDecoder();
        var bpm         = new BassBpmDetector();
        var key         = new HpcpKeyDetector();
        var energy      = new SpectralEnergyDetector();
        var loudness    = new Bs1770LoudnessDetector();
        var reader      = new TagLibMetadataReader();
        var writer      = new TagLibMetadataWriter();

        var analysisService = new TrackAnalysisService(bpm, decoder, key, energy, loudness);
        var engine          = new TrackEngine(analysisService, reader, writer);

        return new EngineComposer(decoder, bpm, analysisService, engine, reader);
    }

    public void Dispose()
    {
        // ManagedBass is a static singleton — freeing it here is correct for a
        // CLI process that exits shortly after. Safe to call multiple times.
        try { ManagedBass.Bass.Free(); } catch { /* best-effort */ }
    }
}
