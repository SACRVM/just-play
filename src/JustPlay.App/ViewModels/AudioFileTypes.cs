namespace JustPlay.App.ViewModels;

/// <summary>
/// The audio file extensions JUST PLAY accepts — queue intake
/// (<see cref="MainWindowViewModel.AddPathsAsync"/>) and the PRE CUE FINDER's folder listing, so
/// the finder can never show a file the queue would then reject.
///
/// <para>0.6: the list itself lives in <see cref="JustPlay.Library.AudioFiles"/>, shared with the
/// scanner and the CLI. It used to be duplicated here, and the two had already drifted — the app
/// accepted <c>.opus</c> and the scanner did not, so the day the finder started listing from the
/// index, every .opus file would have silently disappeared from it.</para>
/// </summary>
internal static class AudioFileTypes
{
    public static bool IsAudio(string path) => JustPlay.Library.AudioFiles.IsAudio(path);
}
