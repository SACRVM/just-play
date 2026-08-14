using System;
using System.Diagnostics;

namespace JustPlay.UI.Controls;

/// <summary>
/// Hand a web address to the user's default browser - the sibling of <see cref="SystemFileBrowser"/>,
/// and shared for the same reason: JUST PLAY's update flow had already written it out inline, and the
/// About card is the second caller.
///
/// <para><b>http and https only.</b> <c>UseShellExecute</c> asks the OS to launch whatever is
/// registered for the string it is given, so a local path or a custom scheme would start a program
/// rather than open a page. That matters because not every caller's string is a literal: the update
/// flow passes a release URL that came back from the GitHub API, i.e. off the network. Anything that
/// is not an absolute http(s) URI is dropped without a sound.</para>
///
/// <para><b>Nothing here throws.</b> A machine with no browser registered is not a reason to take a
/// window down (the suite's never-crash rule) - a failed open simply does nothing visible.</para>
/// </summary>
public static class SystemWebBrowser
{
    /// <summary>Open an absolute http(s) URL in the default browser. Anything else is ignored.</summary>
    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception)
        {
            // See the class remarks: no browser, or a locked-down machine. Not fatal.
        }
    }
}
