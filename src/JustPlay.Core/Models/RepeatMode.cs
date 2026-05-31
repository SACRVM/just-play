namespace JustPlay.Core.Models;

/// <summary>
/// Transport repeat behaviour, cycled by the repeat button (Off → All → One → Off).
/// Mirrors the design's 0/1/2 <c>repeatMode</c> (app.jsx:451).
/// </summary>
public enum RepeatMode
{
    /// <summary>Play to the end of the queue, then stop.</summary>
    Off,

    /// <summary>Loop the whole queue: advancing past the last track wraps to the first.</summary>
    All,

    /// <summary>Repeat the current track when it ends. Manual next/prev still moves.</summary>
    One,
}
