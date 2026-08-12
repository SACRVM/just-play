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
/// <para><b>What <see cref="IsAvailable"/> means, and what it does NOT mean.</b> It is a statement
/// about the PLATFORM: "there is a bin here, and I can reach it". It is not a veto. Where it is
/// false, a delete still happens - it happens permanently, and the window says so in those words
/// before the button is pressed. An app that offers a delete and then refuses to perform one leaves
/// the person in front of it with no way to do a thing the app otherwise offers, and no explanation
/// they can act on. Asking clearly and letting them decide is the better answer.</para>
///
/// <para>There is deliberately still no hard-delete member here: this interface is about the BIN.
/// A permanent removal is a plain <c>File.Delete</c> and belongs to the code that carries the plan
/// out, where it can only be reached through a plan that says so out loud - see
/// <c>JustPlay.Core.Organise.DeleteKind</c>.</para>
/// </summary>
public interface IRecycleBin
{
    /// <summary>
    /// True when this platform has a bin we can actually reach. False means a delete here cannot be
    /// recycled and would have to be permanent - which the preview states plainly rather than
    /// hiding.
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
/// The stand-in for a platform that has no bin at all.
///
/// <para>It carries INFORMATION, not a refusal: <see cref="IsAvailable"/> is false, the planner reads
/// that and plans a permanent delete, and the window says so in the words the user confirms. Handing
/// this to the organise engine is how a host - or a test - states "this machine has nowhere to file
/// a deleted track" without pretending to have one.</para>
///
/// <para><see cref="Send"/> still throws, because there is genuinely nothing to send to. Nothing in
/// the delete path calls it: a plan that says permanent removes the file outright, and a plan that
/// says recycle is never built against a bin that is not there.</para>
/// </summary>
public sealed class NoRecycleBin : IRecycleBin
{
    public static readonly NoRecycleBin Instance = new();

    public bool IsAvailable => false;

    public string Name => "recycle bin";

    public void Send(string path) =>
        throw new PlatformNotSupportedException("There is no recycle bin on this system.");
}
