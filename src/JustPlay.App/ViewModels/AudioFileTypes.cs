using System;
using System.Collections.Generic;
using System.IO;

namespace JustPlay.App.ViewModels;

/// <summary>
/// The audio file extensions JUST PLAY accepts. Single source of truth for the
/// queue intake (<see cref="MainWindowViewModel.AddPathsAsync"/>) and the PRE CUE
/// FINDER's folder listing, so the finder can never show a file the queue would
/// then reject.
/// </summary>
internal static class AudioFileTypes
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif"
    };

    public static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path));
}
