using System;
using System.Threading;
using System.Threading.Tasks;

namespace JustPlay.Core.Updates;

/// <summary>
/// Looks up whether a newer JustPlay release is available. Implementations are
/// transport-only - they never download or install. The app shell owns the download +
/// installer hand-off so <c>JustPlay.Core</c> stays free of process and OS specifics
/// (the same layering rule as the audio / metadata adapters).
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Returns the newest release if it is strictly greater than
    /// <paramref name="current"/>, otherwise null. Never throws for ordinary
    /// network / parse failures - those yield null so the caller can treat
    /// "couldn't check" the same as "nothing new."
    /// </summary>
    Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct);
}
