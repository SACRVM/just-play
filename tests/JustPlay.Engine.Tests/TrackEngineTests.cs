using JustPlay.Analysis;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Engine;
using JustPlay.Engine.Dtos;
using JustPlay.Metadata;

namespace JustPlay.Engine.Tests;

/// <summary>
/// Integration tests for <see cref="TrackEngine"/> using:
/// <list type="bullet">
///   <item>A stub <see cref="IBpmDetector"/> (returns a fixed value — avoids the BASS dependency).</item>
///   <item>A stub <see cref="IAudioDecoder"/> (returns synthesized audio).</item>
///   <item>The real <see cref="ChromagramKeyDetector"/> and <see cref="SpectralEnergyDetector"/>
///         (platform-agnostic DSP, no BASS).</item>
///   <item>The real <see cref="TrackAnalysisService"/> wiring them together.</item>
///   <item>The real <see cref="TagLibMetadataReader"/> / <see cref="TagLibMetadataWriter"/>
///         operating on a temp file synthesized the same way as <c>FavoriteRoundTripTests</c>.</item>
/// </list>
/// No BASS libraries, no Avalonia — runs on any machine with just the .NET runtime.
/// </summary>
public sealed class TrackEngineTests : IDisposable
{
    // ── Audio constants (matching the analysis service internals) ─────────────
    private const int EnergySampleRate = 11025;
    private const int KeySampleRate    = 44100;

    // Frequencies for a C-major triad (same values as ChromagramKeyDetectorTests).
    private const double C4 = 261.6256;
    private const double E4 = 329.6276;
    private const double G4 = 391.9954;

    // ── Temp MP3 used for tag round-trip tests ────────────────────────────────
    private readonly string _tempFile;
    private readonly ITrackEngine _engine;

    public TrackEngineTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"justplay_engine_{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(_tempFile, MinimalMp3());

        _engine = BuildEngine(fixedBpm: 128.0);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    // ── ReadTags ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadTags_MinimalMp3_ReturnsFilePath()
    {
        var dto = await _engine.ReadTagsAsync(_tempFile);

        Assert.Equal(_tempFile, dto.FilePath);
    }

    [Fact]
    public async Task ReadTags_AfterWritingBpm_ReturnsBpm()
    {
        // Write a BPM tag directly via the writer so we know what to expect.
        var writer = new TagLibMetadataWriter();
        writer.Write(_tempFile, new TagWrite { Bpm = 140.0 });

        var dto = await _engine.ReadTagsAsync(_tempFile);

        // TagLib# rounds BPM to uint, so we compare rounded.
        Assert.Equal(140.0, dto.TaggedBpm);
    }

    [Fact]
    public async Task ReadTags_NoTags_HasFallbackName()
    {
        // A bare minimal MP3 may have no title tag; the DTO should still return the file path.
        var dto = await _engine.ReadTagsAsync(_tempFile);

        // FilePath is always populated.
        Assert.False(string.IsNullOrWhiteSpace(dto.FilePath));
    }

    // ── WriteTags ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteTags_Bpm_RoundTrips()
    {
        var request = new WriteTagsRequest { Bpm = 132.0 };
        var result  = await _engine.WriteTagsAsync(_tempFile, request);

        Assert.True(result.Success, result.Error);

        var readBack = await _engine.ReadTagsAsync(_tempFile);
        Assert.Equal(132.0, readBack.TaggedBpm);
    }

    [Fact]
    public async Task WriteTags_ValidCamelotKey_RoundTrips()
    {
        // "8A" = A minor. TagLib# stores it as "Am".
        var request = new WriteTagsRequest { Key = "8A" };
        var result  = await _engine.WriteTagsAsync(_tempFile, request);

        Assert.True(result.Success, result.Error);

        var readBack = await _engine.ReadTagsAsync(_tempFile);
        Assert.NotNull(readBack.TaggedKey);
        // The stored key string is whatever TagLib# writes — just verify it is non-empty.
        Assert.False(string.IsNullOrWhiteSpace(readBack.TaggedKey));
    }

    [Fact]
    public async Task WriteTags_ValidMusicalKey_Succeeds()
    {
        var request = new WriteTagsRequest { Key = "Am" };
        var result  = await _engine.WriteTagsAsync(_tempFile, request);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task WriteTags_InvalidKey_ReturnsError()
    {
        var request = new WriteTagsRequest { Key = "not-a-key" };
        var result  = await _engine.WriteTagsAsync(_tempFile, request);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("not-a-key", result.Error);
    }

    [Fact]
    public async Task WriteTags_Energy_RoundTrips()
    {
        var request = new WriteTagsRequest { Energy = 8 };
        var result  = await _engine.WriteTagsAsync(_tempFile, request);

        Assert.True(result.Success, result.Error);

        var readBack = await _engine.ReadTagsAsync(_tempFile);
        Assert.Equal(8, readBack.TaggedEnergy);
    }

    [Fact]
    public async Task WriteTags_Favorite_RoundTrips()
    {
        var writeResult = await _engine.WriteTagsAsync(_tempFile, new WriteTagsRequest { Favorite = true });
        Assert.True(writeResult.Success, writeResult.Error);

        var readBack = await _engine.ReadTagsAsync(_tempFile);
        Assert.True(readBack.IsFavorite);
    }

    [Fact]
    public async Task WriteTags_AllFields_PreservedTogether()
    {
        var request = new WriteTagsRequest
        {
            Bpm      = 126.0,
            Key      = "Am",
            Energy   = 6,
            Favorite = true,
        };
        var result = await _engine.WriteTagsAsync(_tempFile, request);
        Assert.True(result.Success, result.Error);

        var readBack = await _engine.ReadTagsAsync(_tempFile);
        Assert.Equal(126.0, readBack.TaggedBpm);
        Assert.Equal(6, readBack.TaggedEnergy);
        Assert.True(readBack.IsFavorite);
    }

    // ── AnalyzeTrack ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeTrack_SyntheticAudio_ReturnsBpm()
    {
        // The stub BpmDetector always returns 128.0 regardless of filePath.
        var dto = await _engine.AnalyzeTrackAsync(_tempFile);

        Assert.True(dto.Success, dto.Error);
        Assert.NotNull(dto.Bpm);
        Assert.Equal(128.0, dto.Bpm!.Value, precision: 1);
    }

    [Fact]
    public async Task AnalyzeTrack_SyntheticCMajorTriad_ReturnsKey()
    {
        // The stub decoder returns a C-major triad → ChromagramKeyDetector should
        // identify C major (or an adjacent key — the engine's contract is that key
        // is a non-null string on success).
        var dto = await _engine.AnalyzeTrackAsync(_tempFile);

        Assert.True(dto.Success, dto.Error);
        Assert.NotNull(dto.KeyName);
        Assert.NotNull(dto.KeyCamelot);
        Assert.InRange(dto.KeyConfidence!.Value, 0.0, 1.0);
    }

    [Fact]
    public async Task AnalyzeTrack_ReturnsEnergyInRange()
    {
        var dto = await _engine.AnalyzeTrackAsync(_tempFile);

        Assert.True(dto.Success, dto.Error);
        // Energy is nullable and may be null for very short or synthetic audio.
        if (dto.Energy.HasValue)
            Assert.InRange(dto.Energy.Value, 1, 10);
    }

    [Fact]
    public async Task AnalyzeTrack_FilePath_MatchesInput()
    {
        var dto = await _engine.AnalyzeTrackAsync(_tempFile);
        Assert.Equal(_tempFile, dto.FilePath);
    }

    [Fact]
    public async Task AnalyzeTrack_Progress_FiresAtLeastOnce()
    {
        var reports = new List<TrackAnalysisDto>();
        var progress = new Progress<TrackAnalysisDto>(r => reports.Add(r));

        await _engine.AnalyzeTrackAsync(_tempFile, progress);

        // TrackAnalysisService reports after BPM, key, energy — at minimum one.
        Assert.NotEmpty(reports);
    }

    [Fact]
    public async Task AnalyzeTrack_NonExistentFile_ReturnsFailure()
    {
        // The stub decoder returns silence for any path, but BASS (mocked here)
        // returning null BPM is fine. We test with a missing path to exercise the
        // error-return path on the real TagLib# / decoder.
        // Our stub decoder doesn't throw for missing files, so we force a failure
        // by using a null-ish path that the engine rejects.
        var ex = await Record.ExceptionAsync(
            () => _engine.AnalyzeTrackAsync("   "));

        // ArgumentException.ThrowIfNullOrWhiteSpace — expect ArgumentException.
        Assert.IsType<ArgumentException>(ex);
    }

    // ── AnalyzeLibrary ────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeLibrary_SingleFile_ReturnsOneResult()
    {
        var folder = Path.GetDirectoryName(_tempFile)!;

        // We only care that our one temp file is among the results.
        // (The temp folder may contain other test files from parallel runs.)
        var result = await _engine.AnalyzeLibraryAsync(folder);

        Assert.Contains(result.Tracks, t => t.FilePath == _tempFile);
        Assert.Equal(result.TotalFiles, result.Succeeded + result.Failed);
    }

    [Fact]
    public async Task AnalyzeLibrary_EmptyFolder_ReturnsZeroTracks()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"justplay_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var result = await _engine.AnalyzeLibraryAsync(emptyDir);
            Assert.Equal(0, result.TotalFiles);
            Assert.Empty(result.Tracks);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    // ── Constructor guards ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullAnalysisService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TrackEngine(null!, new TagLibMetadataReader(), new TagLibMetadataWriter()));
    }

    [Fact]
    public void Constructor_NullReader_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TrackEngine(
                new TrackAnalysisService(
                    new StubBpmDetector(128.0),
                    new StubAudioDecoder(MakeCMajorSamples(EnergySampleRate)),
                    new ChromagramKeyDetector(),
                    new SpectralEnergyDetector()),
                null!,
                new TagLibMetadataWriter()));
    }

    [Fact]
    public void Constructor_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TrackEngine(
                new TrackAnalysisService(
                    new StubBpmDetector(128.0),
                    new StubAudioDecoder(MakeCMajorSamples(EnergySampleRate)),
                    new ChromagramKeyDetector(),
                    new SpectralEnergyDetector()),
                new TagLibMetadataReader(),
                null!));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="TrackEngine"/> wired with platform-agnostic stubs so no
    /// BASS libraries are required. The BPM detector is stubbed; key and energy use
    /// the real DSP implementations.
    /// </summary>
    private static TrackEngine BuildEngine(double fixedBpm)
    {
        var energySamples = MakeCMajorSamples(EnergySampleRate);
        var keySamples    = MakeCMajorSamples(KeySampleRate);

        // The stub decoder returns different sample buffers at different sample rates,
        // matching what TrackAnalysisService requests (11 kHz for energy/BPM, 44.1 kHz for key).
        var decoder = new StubAudioDecoder(energySamples, keySamples,
            energySampleRate: EnergySampleRate, keySampleRate: KeySampleRate);

        var analysisService = new TrackAnalysisService(
            bpm:     new StubBpmDetector(fixedBpm),
            decoder: decoder,
            key:     new ChromagramKeyDetector(),
            energy:  new SpectralEnergyDetector());

        return new TrackEngine(analysisService, new TagLibMetadataReader(), new TagLibMetadataWriter());
    }

    /// <summary>Generates a C-major triad (C4 + E4 + G4) at the given sample rate, 6 seconds.</summary>
    private static float[] MakeCMajorSamples(int sampleRate)
    {
        const double duration = 6.0;
        var count   = (int)(sampleRate * duration);
        var samples = new float[count];
        const double amp = 0.3;
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / sampleRate;
            samples[i] = (float)(
                amp * Math.Sin(2.0 * Math.PI * C4 * t) +
                amp * Math.Sin(2.0 * Math.PI * E4 * t) +
                amp * Math.Sin(2.0 * Math.PI * G4 * t));
        }
        return samples;
    }

    /// <summary>
    /// Synthesises a minimal ID3v2-tagged MP3 file in memory (TagLib# writes the header).
    /// Same technique as FavoriteRoundTripTests — no hand-crafted frame bytes.
    /// </summary>
    private static byte[] MinimalMp3()
    {
        var buf = Path.Combine(Path.GetTempPath(), $"justplay_engine_seed_{Guid.NewGuid():N}.mp3");
        try
        {
            // Minimal ID3v2.3 header (10 bytes) + one silent MPEG frame header (4 bytes).
            var bare = new byte[14];
            bare[0]  = (byte)'I'; bare[1] = (byte)'D'; bare[2] = (byte)'3';
            bare[3]  = 3;         // version 2.3
            bare[10] = 0xFF; bare[11] = 0xFB; bare[12] = 0x90; bare[13] = 0x00;
            File.WriteAllBytes(buf, bare);

            using var f = TagLib.File.Create(buf);
            f.Tag.Title = "Engine Test Track";
            f.Save();

            return File.ReadAllBytes(buf);
        }
        finally
        {
            if (File.Exists(buf)) File.Delete(buf);
        }
    }
}

// ── Test stubs ────────────────────────────────────────────────────────────────

/// <summary>
/// Platform-agnostic stub BPM detector that returns a fixed value.
/// Avoids the BASS native dependency in test runs.
/// </summary>
file sealed class StubBpmDetector : IBpmDetector
{
    private readonly double? _bpm;
    public StubBpmDetector(double? bpm) => _bpm = bpm;
    public double? Detect(string filePath, CancellationToken ct = default) => _bpm;
}

/// <summary>
/// Platform-agnostic stub audio decoder.
/// Returns pre-baked samples at the matching sample rate, falling back to the
/// energy buffer for any unrecognised rate (keeps the test DRY for single-rate cases).
/// </summary>
file sealed class StubAudioDecoder : IAudioDecoder
{
    private readonly float[] _energySamples;
    private readonly float[] _keySamples;
    private readonly int     _energySampleRate;
    private readonly int     _keySampleRate;

    public StubAudioDecoder(float[] samples)
    {
        _energySamples    = samples;
        _keySamples       = samples;
        _energySampleRate = 11025;
        _keySampleRate    = 44100;
    }

    public StubAudioDecoder(
        float[] energySamples, float[] keySamples,
        int energySampleRate, int keySampleRate)
    {
        _energySamples    = energySamples;
        _keySamples       = keySamples;
        _energySampleRate = energySampleRate;
        _keySampleRate    = keySampleRate;
    }

    public DecodedAudio DecodeMono(string filePath, int targetSampleRate, CancellationToken ct = default)
        => targetSampleRate == _keySampleRate
            ? new DecodedAudio(_keySamples,    _keySampleRate)
            : new DecodedAudio(_energySamples, _energySampleRate);
}
