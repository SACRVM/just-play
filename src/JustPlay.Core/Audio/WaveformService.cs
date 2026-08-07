using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Abstractions;

namespace JustPlay.Core.Audio;

/// <summary>
/// <see cref="IWaveformService"/> over <see cref="IAudioDecoder"/> - decodes a file to mono samples and
/// reduces them to a normalised peak envelope. Platform-agnostic (Core): the only audio dependency is the
/// injected decoder, so this same service feeds the Avalonia <c>WaveformView</c> today and the JUST SPIN
/// decks later. A small bounded cache makes re-cueing a track instant.
/// </summary>
public sealed class WaveformService : IWaveformService
{
    // A scrubber overview doesn't need full fidelity: decoding mono at a low rate turns a 6-minute track
    // into a few million samples instead of tens of millions, and the bucketed envelope looks identical.
    private const int DecodeRate = 11025;

    // How many recent (path+resolution) waveforms to keep - the finder cues one at a time, so a handful
    // covers re-cueing and nudging back and forth without unbounded growth.
    private const int CacheCap = 12;

    private readonly IAudioDecoder _decoder;
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _order = new();

    public WaveformService(IAudioDecoder decoder) => _decoder = decoder;

    public Task<float[]> ComputeAsync(string filePath, int buckets, CancellationToken ct = default)
    {
        if (buckets < 1) buckets = 1;
        var key = $"{buckets}|{filePath}";
        if (_cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var audio = _decoder.DecodeMono(filePath, DecodeRate, ct);
            var peaks = ToPeaks(audio.Samples, buckets, ct);
            Store(key, peaks);
            return peaks;
        }, ct);
    }

    /// <summary>Reduce samples to <paramref name="buckets"/> per-bucket peaks, then normalise so the loudest
    /// bucket is 1.0 - quiet tracks still fill the bar height (the FUVI / SoundCloud look).</summary>
    private static float[] ToPeaks(float[] samples, int buckets, CancellationToken ct)
    {
        var peaks = new float[buckets];
        if (samples.Length == 0) return peaks;

        double max = 0;
        for (var b = 0; b < buckets; b++)
        {
            ct.ThrowIfCancellationRequested();
            // Integer-exact bucket boundaries (long math avoids overflow on very long tracks).
            var start = (int)((long)b * samples.Length / buckets);
            var end = (int)((long)(b + 1) * samples.Length / buckets);
            if (end <= start) end = Math.Min(start + 1, samples.Length);

            float peak = 0;
            for (var i = start; i < end; i++)
            {
                var a = Math.Abs(samples[i]);
                if (a > peak) peak = a;
            }
            peaks[b] = peak;
            if (peak > max) max = peak;
        }

        if (max > 0)
        {
            var inv = (float)(1.0 / max);
            for (var b = 0; b < buckets; b++) peaks[b] *= inv;
        }
        return peaks;
    }

    private void Store(string key, float[] peaks)
    {
        if (!_cache.TryAdd(key, peaks)) return;
        _order.Enqueue(key);
        while (_order.Count > CacheCap && _order.TryDequeue(out var old))
            _cache.TryRemove(old, out _);
    }
}
