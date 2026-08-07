using System.Threading;
using System.Threading.Tasks;
using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// What the SERVER says about a mount right now - how many are listening, what it thinks is playing.
///
/// <para>(!) Read "Listeners" honestly: Icecast counts CONNECTIONS, not people. A listener whose
/// connection drops and resumes is two; a player that buffers by opening a second stream is two. It
/// is a good trend ("is anyone there", "did the post land") and a bad headcount, and the UI should
/// not pretend otherwise.</para>
/// </summary>
/// <param name="Listeners">Current connections on the mount.</param>
/// <param name="PeakListeners">The server's high-water mark since the source connected.</param>
/// <param name="Title">What the server believes is playing (from the source's metadata updates).</param>
/// <param name="BitrateKbps">Bitrate the source is delivering, if the server reports one.</param>
/// <param name="ServerId">e.g. "Icecast 2.4.0-kh22" - useful in a bug report, useless on screen.</param>
public sealed record StreamStats(
    int Listeners,
    int PeakListeners,
    string? Title = null,
    int? BitrateKbps = null,
    string? ServerId = null);

/// <summary>
/// Asks an Icecast server for a mount's listener stats.
///
/// <para><b>Why this exists as its own thing.</b> The source protocol is a one-way street: we push
/// audio up and the server tells us nothing back except whether the connection lives. Listener counts
/// live on a SEPARATE, ordinary HTTP endpoint on the same server. Every broadcaster does it this way;
/// it is not a trick.</para>
///
/// <para>Deliberately NOT part of <see cref="IBroadcastService"/>: that one owns the encoder and the
/// socket and is implemented on BASS. This is an HTTP GET and a parse, it needs no audio stack, and
/// keeping it separate means the stats can be polled while NOT broadcasting - which is exactly when
/// you want to know whether someone else's source is on your mount.</para>
/// </summary>
public interface IStreamStatsProbe
{
    /// <summary>
    /// The mount's stats, or null when the server cannot be reached, hides its status page, or does
    /// not list this mount. Never throws for an unreachable server: an unknown listener count is a
    /// dash on screen, not an error dialog.
    /// </summary>
    Task<StreamStats?> TryReadAsync(StreamServerProfile server, CancellationToken ct = default);
}
