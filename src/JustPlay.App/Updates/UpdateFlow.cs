using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using JustPlay.App.ViewModels;
using JustPlay.App.Views;
using JustPlay.Core.Updates;
using JustPlay.UI.Controls;

namespace JustPlay.App.Updates;

/// <summary>
/// Drives what happens when the user clicks the title-bar update badge: show the themed
/// <see cref="UpdateDialog"/>, then act on the choice - silently download + install + relaunch
/// (installer builds), open the release page (portable builds), or persist "ignore this version".
/// Lives in the App layer because it needs the owner window and shuts the app down; the state it
/// reads (<see cref="UpdateViewModel.Available"/>) comes from the polling VM.
/// </summary>
internal static class UpdateFlow
{
    public static async Task ShowAndApplyAsync(Window owner, UpdateViewModel update, HttpClient http)
    {
        if (update.Available is not { } info) return;

        var current = typeof(UpdateFlow).Assembly.GetName().Version;
        var canSelfUpdate = !string.IsNullOrEmpty(info.InstallerAssetUrl)
                            && UpdateInstaller.TryGetInnoInstallDir(out _);

        var choice = await UpdateDialog.AskAsync(owner, current, info, canSelfUpdate);

        switch (choice)
        {
            case UpdateDialog.Choice.Install:
                await InstallAsync(info, canSelfUpdate, http);
                break;
            case UpdateDialog.Choice.Ignore:
                update.IgnoreCurrent();
                break;
            case UpdateDialog.Choice.Later:
                // Leave the badge up; ask again next time it's clicked / next check.
                break;
        }
    }

    private static async Task InstallAsync(UpdateInfo info, bool canSelfUpdate, HttpClient http)
    {
        if (!canSelfUpdate || !UpdateInstaller.TryGetInnoInstallDir(out var installDir))
        {
            OpenReleasePage(info.ReleaseUrl);
            return;
        }

        try
        {
            var installerPath = await UpdateInstaller.DownloadAsync(info, http, CancellationToken.None);
            // Hands off to the detached helper and shuts JustPlay down so its files unlock.
            UpdateInstaller.LaunchAndExit(installerPath, installDir);
        }
        catch
        {
            // Download / hand-off failed - fall back to the manual route so the user isn't stuck.
            OpenReleasePage(info.ReleaseUrl);
        }
    }

    // The shared opener rather than a local Process.Start: it accepts absolute http(s) only, and
    // this URL came back from the GitHub API - the one caller in the suite whose string is not a
    // literal. A refused open leaves the badge up, so the user can retry.
    private static void OpenReleasePage(string url) => SystemWebBrowser.Open(url);
}
