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

            // BASS honours BassFlags.Mono only for formats whose decoder implements it
            // (the MP3 family + OGG). Add-on codecs (FLAC, ...) and WAV/AIFF IGNORE the
            // flag and deliver interleaved multi-channel data - silently, with
            // info.Channels telling the truth. Downmix HERE so the mono contract holds
            // for every format. (Found 2026-07-10 via the beatbed click-check: FLAC
            // "mono" decodes returned 2x samples -> half-speed renders, and every
            // sample-based analyzer saw octave-shifted pseudo-audio on FLAC input.)
            var channels = Math.Max(1, info.Channels);

            var mono = ReadAllSamples(handle, channels, ct);
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

    private static float[] ReadAllSamples(int handle, int channels, CancellationToken ct)
    {
        const int ChunkFloats = 1 << 16; // 64k floats per read
        var buffer = new float[ChunkFloats];
        var all = new List<float>(ChunkFloats * 4);

        // Carry for a sample frame split across two reads (BASS returns whole floats,
        // but not necessarily whole FRAMES per call).
        var pending      = new float[channels];
        var pendingCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var bytesRead = ManagedBass.Bass.ChannelGetData(handle, buffer, ChunkFloats * sizeof(float));
            if (bytesRead <= 0)
                break; // -1 = ended/error; 0 = no data

            var floatsRead = bytesRead / sizeof(float);

            if (channels == 1)
            {
                all.AddRange(floatsRead == buffer.Length ? buffer : buffer[..floatsRead]);
                continue;
            }

            var idx = 0;

            // Complete a frame left over from the previous read.
            while (pendingCount > 0 && pendingCount < channels && idx < floatsRead)
                pending[pendingCount++] = buffer[idx++];
            if (pendingCount == channels)
            {
                var pSum = 0f;
                for (var c = 0; c < channels; c++) pSum += pending[c];
                all.Add(pSum / channels);
                pendingCount = 0;
            }

            // Whole frames in this read -> average channels down to one sample.
            while (idx + channels <= floatsRead)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++) sum += buffer[idx + c];
                all.Add(sum / channels);
                idx += channels;
            }

            // Stash a trailing partial frame for the next read.
            while (idx < floatsRead)
                pending[pendingCount++] = buffer[idx++];
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
