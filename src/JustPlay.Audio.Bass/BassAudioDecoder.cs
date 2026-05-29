using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using ManagedBass;

namespace JustPlay.Audio.Bass;

/// <summary>
/// Decodes a file to mono float samples using a BASS decode stream (no audio output),
/// then resamples to the requested rate. Feeds the DSP analyzers.
/// </summary>
public sealed class BassAudioDecoder : IAudioDecoder
{
    public DecodedAudio DecodeMono(string filePath, int targetSampleRate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetSampleRate);

        // Decode (no playback) + downmix to mono + 32-bit float samples.
        var handle = ManagedBass.Bass.CreateStream(
            filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Mono);
        if (handle == 0)
            throw new InvalidOperationException($"Could not decode '{filePath}': {ManagedBass.Bass.LastError}");

        try
        {
            var info = ManagedBass.Bass.ChannelGetInfo(handle);
            var sourceRate = info.Frequency;

            var mono = ReadAllSamples(handle, ct);
            var resampled = sourceRate == targetSampleRate
                ? mono
                : Resample(mono, sourceRate, targetSampleRate);

            return new DecodedAudio(resampled, targetSampleRate);
        }
        finally
        {
            ManagedBass.Bass.StreamFree(handle);
        }
    }

    private static float[] ReadAllSamples(int handle, CancellationToken ct)
    {
        const int ChunkFloats = 1 << 16; // 64k floats per read
        var buffer = new float[ChunkFloats];
        var all = new List<float>(ChunkFloats * 4);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var bytesRead = ManagedBass.Bass.ChannelGetData(handle, buffer, ChunkFloats * sizeof(float));
            if (bytesRead <= 0)
                break; // -1 = ended/error; 0 = no data

            var floatsRead = bytesRead / sizeof(float);
            all.AddRange(floatsRead == buffer.Length ? buffer : buffer[..floatsRead]);
        }

        return [.. all];
    }

    /// <summary>Linear-interpolation resample. Adequate for BPM/key/energy feature extraction.</summary>
    private static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0) return input;

        var ratio = (double)targetRate / sourceRate;
        var outLength = (int)(input.Length * ratio);
        var output = new float[outLength];

        for (var i = 0; i < outLength; i++)
        {
            var srcPos = i / ratio;
            var i0 = (int)srcPos;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = (float)(srcPos - i0);
            output[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }

        return output;
    }
}
