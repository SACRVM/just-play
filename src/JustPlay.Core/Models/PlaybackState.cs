namespace JustPlay.Core.Models;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused
}

/// <summary>Decoded mono audio for analysis.</summary>
/// <param name="Samples">Interleaved-free mono samples in [-1, 1].</param>
/// <param name="SampleRate">Samples per second.</param>
public readonly record struct DecodedAudio(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}
