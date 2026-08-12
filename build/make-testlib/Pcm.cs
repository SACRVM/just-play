using System.Buffers.Binary;

namespace MakeTestLib;

/// <summary>
/// Everything that turns a seed into audible bytes. Pure arithmetic, no randomness that could
/// drift between runs: the same seed string always yields the same samples, so a reset really
/// rebuilds the identical tree instead of a similar one.
/// </summary>
internal static class Pcm
{
    public const int SampleRate = 44100;
    public const int Channels = 2;

    /// <summary>Peak amplitude of the synthesised tone, about -18 dBFS. Deliberately quiet:
    /// these files get previewed on headphones, one after another, dozens of times.</summary>
    private const double Amplitude = 0.12;

    /// <summary>Lengths a file can have, in seconds. Varying the length is half of what makes
    /// two files tellable apart in the preview; the pitch is the other half.</summary>
    private static readonly double[] Lengths = [2.1, 2.6, 3.2, 3.8, 4.4, 5.1, 6.0, 7.0];

    /// <summary>Pitches, in Hz. Low and consonant on purpose - a bench file should not be a
    /// harsh sine at 4 kHz in somebody's ears.</summary>
    private static readonly double[] Pitches =
        [174.6, 196.0, 220.0, 246.9, 261.6, 293.7, 329.6, 349.2, 392.0, 440.0];

    /// <summary>Pulse rates, in Hz - the slow amplitude wobble that makes a tone sound like a
    /// file and not like a test signal stuck on.</summary>
    private static readonly double[] Pulses = [1.0, 1.5, 2.0, 2.5];

    public static double SecondsFor(uint seed) => Lengths[(int)(seed % (uint)Lengths.Length)];

    /// <summary>
    /// Interleaved 16-bit stereo little-endian samples: exactly the payload a WAV data chunk
    /// carries, and exactly what the BASS encoders are fed.
    /// </summary>
    public static byte[] Render(uint seed)
    {
        var seconds = SecondsFor(seed);
        var pitch = Pitches[(int)(seed / 8 % (uint)Pitches.Length)];
        var pulse = Pulses[(int)(seed / 128 % (uint)Pulses.Length)];

        var frames = (int)Math.Round(seconds * SampleRate);
        var bytes = new byte[frames * Channels * 2];
        var fade = (int)(0.03 * SampleRate);

        for (var n = 0; n < frames; n++)
        {
            var t = n / (double)SampleRate;

            // Slow tremolo between half and full level.
            var env = 0.5 + 0.5 * (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * pulse * t));

            // Linear fade in / out so no file starts or ends on a click.
            if (n < fade) env *= n / (double)fade;
            else if (n >= frames - fade) env *= (frames - 1 - n) / (double)fade;

            var phase = 2.0 * Math.PI * pitch * t;
            var l = Math.Sin(phase);
            var r = Math.Sin(phase + Math.PI / 2.0); // quarter turn apart = a little width

            Write16(bytes, (n * Channels + 0) * 2, l * env * Amplitude);
            Write16(bytes, (n * Channels + 1) * 2, r * env * Amplitude);
        }

        return bytes;
    }

    private static void Write16(byte[] target, int offset, double value)
    {
        var v = (int)Math.Round(Math.Clamp(value, -1.0, 1.0) * short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), (short)v);
    }

    /// <summary>Canonical 44.1 kHz / 16-bit / stereo RIFF WAVE around the given samples.</summary>
    public static void WriteWav(string path, byte[] samples)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        var byteRate = SampleRate * Channels * 2;

        w.Write("RIFF"u8);
        w.Write(36 + samples.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                       // PCM fmt chunk size
        w.Write((short)1);                 // WAVE_FORMAT_PCM
        w.Write((short)Channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write((short)(Channels * 2));    // block align
        w.Write((short)16);                // bits per sample
        w.Write("data"u8);
        w.Write(samples.Length);
        w.Write(samples);
    }

    /// <summary>
    /// Canonical AIFF (FORM / COMM / SSND). AIFF samples are BIG endian, and the COMM sample rate
    /// is an 80-bit IEEE extended float - 44100 is exponent 0x400E with mantissa 0xAC44 shifted up
    /// to bit 63, which is the constant spelled out below.
    /// </summary>
    public static void WriteAiff(string path, byte[] samples)
    {
        var frames = samples.Length / (Channels * 2);
        var ssndSize = 8 + samples.Length;          // offset + blockSize + data
        var formSize = 4 + (8 + 18) + (8 + ssndSize);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        w.Write("FORM"u8);
        WriteBe32(w, formSize);
        w.Write("AIFF"u8);

        w.Write("COMM"u8);
        WriteBe32(w, 18);
        WriteBe16(w, (short)Channels);
        WriteBe32(w, frames);
        WriteBe16(w, 16);                            // sample size
        w.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }); // 44100 Hz, extended

        w.Write("SSND"u8);
        WriteBe32(w, ssndSize);
        WriteBe32(w, 0);                             // offset
        WriteBe32(w, 0);                             // block size

        var be = new byte[samples.Length];
        for (var i = 0; i < samples.Length; i += 2)
        {
            be[i] = samples[i + 1];
            be[i + 1] = samples[i];
        }

        w.Write(be);
        if (samples.Length % 2 != 0) w.Write((byte)0); // IFF chunks are even sized
    }

    private static void WriteBe32(BinaryWriter w, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        w.Write(b);
    }

    private static void WriteBe16(BinaryWriter w, short value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, value);
        w.Write(b);
    }
}
