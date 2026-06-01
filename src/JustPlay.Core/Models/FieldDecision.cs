namespace JustPlay.Core.Models;

/// <summary>
/// The user's decision for one analysis field (BPM / Key / Energy) on a track,
/// recorded in the file's JUSTPLAY tag so the picture survives an app restart.
/// </summary>
public enum FieldDecision
{
    /// <summary>Detected value differs from the file's tag and the user hasn't
    /// decided yet — shown bold in the queue.</summary>
    Pending,

    /// <summary>User wrote our detected value into the standard tag (we own it).</summary>
    Applied,

    /// <summary>User reviewed and kept the original/claimed tag value — stop flagging.</summary>
    Kept,
}
