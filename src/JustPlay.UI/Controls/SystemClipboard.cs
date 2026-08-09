using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace JustPlay.UI.Controls;

/// <summary>
/// Putting text on the clipboard, once, for the whole suite - the sibling of
/// <see cref="SystemFileBrowser"/>.
///
/// <para>(!) THIS EXISTS BECAUSE OF A MEASURED CRASH, not out of defensive habit. The Win32
/// clipboard raises a <c>COMException</c> from inside <c>SetDataAsync</c> when the OS will not hand
/// it over ("0x800401F0: CoInitialize has not been called" was the observed one), and an exception
/// escaping an <c>async void</c> event handler on the UI thread TERMINATES THE PROCESS. A copy that
/// did not happen is worth one word in the window; it is never worth the app.</para>
///
/// <para>It also ends a split: the suite had two ways of doing this - the
/// <c>DataTransfer</c> shape in the log and crash dialogs, and the obsolete <c>SetTextAsync</c>
/// extension in JUST TAG. Two dialects for one action is how three of the four call sites ended up
/// unguarded while the fourth was fine.</para>
///
/// <para>Avalonia 12 moved the clipboard to <c>DataTransfer</c>; <c>SetTextAsync</c> survives only
/// as an extension (verified in <c>src/Avalonia.Base/Input/Platform/ClipboardExtensions.cs</c>,
/// release/12.0.3). The transfer object is the direct route, so that is what this uses.</para>
/// </summary>
public static class SystemClipboard
{
    /// <summary>
    /// Copy <paramref name="text"/>, and say whether it worked. Never throws - callers are event
    /// handlers. Empty text and a missing clipboard both count as "did not copy", because from where
    /// the user is standing they are the same thing.
    /// </summary>
    public static async Task<bool> CopyTextAsync(TopLevel? owner, string? text)
    {
        if (owner?.Clipboard is not { } clipboard || string.IsNullOrEmpty(text)) return false;

        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
            return true;
        }
        catch (Exception)
        {
            // The OS refused the clipboard - another process may be holding it. Nothing here is
            // recoverable and nothing here is worth a dialog; the caller shows a word.
            return false;
        }
    }
}
