using JustPlay.Analysis;
using JustPlay.Audio.Bass;
using JustPlay.Core.Abstractions;
using JustPlay.Engine;
using JustPlay.Metadata;
using JustPlay.ML;

namespace JustPlay.Cli;

/// <summary>
/// Composes the JustPlay analysis stack for headless (CLI / MCP) use without DI.
///
/// <para>
/// Mirrors the wiring in <c>JustPlay.App.Program.ConfigureServices</c> but skips
/// Avalonia, ISettingsService, and all UI-only services. Key detection uses the SAME
/// <see cref="BestKeyDetector"/> as the app (ML model when available, else DSP) - so a
/// track keyed by the CLI shows the identical key in the UI (no key-conflict dots from a
/// CLI/app detector mismatch). It falls back to DSP automatically if the ONNX runtime/model
/// is absent in a headless environment.
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
    /// <summary>
    /// Direct access to the metadata writer, used by the promote command to write
    /// the JUSTPLAY blob (TrackAnalysisState) + standard tags in a single file save -
    /// the Engine facade's WriteTagsAsync does not expose the blob field.
    /// </summary>
    public IMetadataWriter       MetadataWriter  { get; }
    /// <summary>
    /// Direct access to the audio decoder for commands that need raw PCM samples
    /// (e.g. <c>spectrum</c>) without going through the full analysis pipeline.
    /// BASS is guaranteed to be initialised by the time this is accessible.
    /// </summary>
    public IAudioDecoder         Decoder         { get; }

    private EngineComposer(
        BassAudioDecoder decoder,
        BassBpmDetector bpmDetector,
        ITrackAnalysisService analysisService,
        ITrackEngine engine,
        IMetadataReader metadataReader,
        IMetadataWriter metadataWriter)
    {
        _decoder         = decoder;
        _bpmDetector     = bpmDetector;
        AnalysisService  = analysisService;
        Engine           = engine;
        MetadataReader   = metadataReader;
        MetadataWriter   = metadataWriter;
        Decoder          = decoder;
    }

    /// <summary>
    /// Constructs and returns a composed analysis stack ready for use.
    /// Initialises the BASS library (no output device - decode-only mode via BassAudioDecoder).
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
        // Same canonical detector as the app: ML model when available, else DSP template.
        var key         = new BestKeyDetector();
        var energy      = new SpectralEnergyDetector();
        var loudness    = new Bs1770LoudnessDetector();
        var reader      = new TagLibMetadataReader();
        var writer      = new TagLibMetadataWriter();

        var analysisService = new TrackAnalysisService(bpm, decoder, key, energy, loudness);
        var engine          = new TrackEngine(analysisService, reader, writer);

        return new EngineComposer(decoder, bpm, analysisService, engine, reader, writer);
    }

    public void Dispose()
    {
        // ManagedBass is a static singleton - freeing it here is correct for a
        // CLI process that exits shortly after. Safe to call multiple times.
        try { ManagedBass.Bass.Free(); } catch { /* best-effort */ }
    }
}
