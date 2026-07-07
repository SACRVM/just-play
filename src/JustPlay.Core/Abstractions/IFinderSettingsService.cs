using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Loads and saves the PRE CUE FINDER's add-on settings. Separate from
/// <see cref="ISettingsService"/> on purpose — the finder must not water down the
/// main settings file (own file, own lifecycle). Same contract shape: the
/// constructor loads, <see cref="Save"/> replaces and persists atomically.
/// </summary>
public interface IFinderSettingsService
{
    /// <summary>
    /// The settings currently in memory. Records are immutable — build a new one
    /// (<c>Current with { … }</c>) and call <see cref="Save"/> to persist.
    /// </summary>
    FinderSettings Current { get; }

    /// <summary>Replace <see cref="Current"/> and write it to disk (atomic).</summary>
    void Save(FinderSettings settings);
}
