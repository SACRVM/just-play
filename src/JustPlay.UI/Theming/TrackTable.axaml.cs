using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace JustPlay.UI.Theming;

/// <summary>
/// The shared track-table layout tokens (column widths, row margins) and the cell converters, as a
/// typed, mergeable <see cref="ResourceDictionary"/>. Every app that shows a list of tracks merges it
/// into <c>Application.Resources</c> next to <see cref="JustPalette"/>, so
/// <see cref="Controls.TrackRow"/> and <see cref="Controls.TrackDataHeader"/> find the same widths in
/// JUST PLAY's queue, the PRE CUE FINDER and JUST TAG.
/// </summary>
public partial class TrackTable : ResourceDictionary
{
    public TrackTable() => AvaloniaXamlLoader.Load(this);
}
