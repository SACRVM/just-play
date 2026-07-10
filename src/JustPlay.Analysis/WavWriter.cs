namespace JustPlay.Analysis;

/// <summary>
/// Minimal 16-bit PCM RIFF/WAVE writer for analysis artifacts — click-check renders,
/// carrier previews. Same role as <see cref="PngWriter"/> fills for plots: plain
/// spec-conformant bytes, no NuGet, no BASS, platform-agnostic, trim-/AOT-safe.
///
/// <para>Samples are hard-clipped to [-1, 1] and quantised to 16 bit. Deliberately mono
/// (the click-check ear test needs timing, not imaging); a stereo overload can join when
/// the carrier renderer needs it.</para>
/// </summary>
public static class WavWriter
{
    /// <summary>Writes mono float samples as a 16-bit PCM WAV file (overwrites).</summary>
    public static void WriteMono16(string path, float[] samples, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0)
            throw new ArgumentException("No samples to write.", nameof(samples));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        var dataBytes = samples.Length * 2;

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);

        // fmt chunk — PCM, mono, 16 bit
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);                 // wFormatTag = PCM
        bw.Write((short)1);                 // nChannels
        bw.Write(sampleRate);               // nSamplesPerSec
        bw.Write(sampleRate * 2);           // nAvgBytesPerSec (mono 16 bit)
        bw.Write((short)2);                 // nBlockAlign
        bw.Write((short)16);                // wBitsPerSample

        // data chunk
        bw.Write("data"u8);
        bw.Write(dataBytes);
        foreach (var s in samples)
        {
            var v = Math.Clamp(s, -1f, 1f);
            bw.Write((short)Math.Round(v * 32767f));
        }
    }
}
