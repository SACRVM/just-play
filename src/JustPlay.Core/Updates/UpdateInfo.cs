using System;

namespace JustPlay.Core.Updates;

/// <summary>
/// A newer release discovered on GitHub. <see cref="Version"/> is normalised to
/// Major.Minor.Build. <see cref="InstallerAssetUrl"/> points at the
/// <c>JustPlaySetup-*.exe</c> attached to the release; it is null when the release
/// has no installer asset (then the shell falls back to opening <see cref="ReleaseUrl"/>
/// in the browser).
/// </summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleaseUrl,
    string? ReleaseNotes,
    string? InstallerAssetUrl,
    string? InstallerAssetName,
    long InstallerAssetSize);
