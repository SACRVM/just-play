using JustPlay.Core.Models;

namespace JustPlay.Core.Abstractions;

/// <summary>
/// Loads and saves user preferences. Implementations choose the storage
/// medium - JSON file in <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// is the production pick because it's cross-platform and survives uninstalls
/// (the user's Sunset choice is still there after a reinstall). Tests can
/// substitute an in-memory implementation.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// The settings currently in memory. Mutating this directly does NOT
    /// persist - call <see cref="Save"/>. Initial value comes from the
    /// constructor reading the on-disk file.
    /// </summary>
    UserSettings Current { get; }

    /// <summary>
    /// Replace <see cref="Current"/> with <paramref name="settings"/> and
    /// write it to disk. Atomic on the file system - partial writes can
    /// never leave the settings file half-corrupted.
    /// </summary>
    void Save(UserSettings settings);
}
