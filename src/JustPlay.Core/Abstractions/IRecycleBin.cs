using System;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// The operating system's own "put this where I can get it back" bin - Windows' Recycle Bin, the
/// Mac's Trash.
///
/// <para><b>Why this is an interface and not three lines of P/Invoke.</b> Recycling a file needs a
/// platform API, and this project may not contain one (CLAUDE.md's layering rule). The seam sits
/// here beside the other platform abstractions and the implementation lives with the other platform
/// shims, exactly as "reveal in the file manager" already does.</para>
///
/// <para><b>(!!) There is no hard-delete member, and there must never be one.</b> Deleting a track
/// for good is not a feature this suite offers: the whole point of routing a delete through the OS
/// bin is that a wrong click on a Friday night is a trip to the bin and not a re-purchase. Where no
/// bin can be reached, <see cref="IsAvailable"/> is false and the operation REFUSES - it does not
/// quietly fall back to <c>File.Delete</c>.</para>
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// True when this platform has a bin we can actually reach. False makes every delete refuse
    /// before it starts, with the reason on screen.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// What the bin is CALLED here - "Recycle Bin" on Windows, "Trash" on macOS. The UI has to say
    /// the words, because a DJ must never be left wondering whether the track is gone or filed.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Send one file to the bin. Throws when it did not happen - the caller records the failure per
    /// file and keeps going, so one stubborn file never stops a batch.
    /// </summary>
    void Send(string path);
}

/// <summary>
/// The bin on a platform that has none. Every delete built on it refuses, which is the entire
/// contract: a missing bin must never silently become a permanent delete.
/// </summary>
public sealed class NoRecycleBin : IRecycleBin
{
    public static readonly NoRecycleBin Instance = new();

    public bool IsAvailable => false;

    public string Name => "recycle bin";

    public void Send(string path) =>
        throw new PlatformNotSupportedException(
            "There is no recycle bin on this system, and a track is never deleted for good.");
}
