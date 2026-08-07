namespace JustPlay.Audio.Bass;

/// <summary>
/// A BASS audio source that owns a persistent output mixer the Icecast encoder
/// (<see cref="BassBroadcastService"/>) can tap. Both the playback engine
/// (<see cref="BassAudioEngine"/>, JustPlay) and the input-capture engine
/// (<see cref="BassInputCaptureEngine"/>, JUST STREAM) implement this, so the same
/// broadcast service casts whichever one is wired up at the DI root.
///
/// This abstraction deliberately lives in the BASS adapter assembly, NOT in
/// JustPlay.Core - a raw BASS channel handle is a platform concept and Core must
/// stay platform-agnostic (CLAUDE.md layering rule).
/// </summary>
public interface IBassMixerSource
{
    /// <summary>
    /// The persistent BASS mixer channel handle the encoder attaches to (post-DSP).
    /// 0 when the mixer has not been created yet (no track loaded / capture not started).
    /// </summary>
    int OutputChannel { get; }
}
