using System.Collections.ObjectModel;
using System.Text;
using JustPlay.Core.Models;

namespace JustPlay.Core.Sets;

/// <summary>
/// The "Set Maker": an ordered, in-memory collection the user assembles by hand.
/// Nothing is persisted automatically — but a set can be exported to a standard
/// .m3u8 playlist on demand. This is the one place JustPlay produces a saved artifact,
/// and only because the user explicitly asks for it.
/// </summary>
public sealed class SetBuilder
{
    private readonly ObservableCollection<Track> _tracks = [];

    public ReadOnlyObservableCollection<Track> Tracks { get; }

    public SetBuilder()
    {
        Tracks = new ReadOnlyObservableCollection<Track>(_tracks);
    }

    public void Add(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _tracks.Add(track);
    }

    public void Insert(int index, Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _tracks.Insert(Math.Clamp(index, 0, _tracks.Count), track);
    }

    public void Remove(Track track) => _tracks.Remove(track);

    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _tracks.Count) return;
        newIndex = Math.Clamp(newIndex, 0, _tracks.Count - 1);
        _tracks.Move(oldIndex, newIndex);
    }

    public void Clear() => _tracks.Clear();

    /// <summary>Render the current set as extended M3U (UTF-8) text, ready to write to a .m3u8 file.</summary>
    public string ToM3U()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var t in _tracks)
        {
            var seconds = (int)Math.Round(t.Metadata?.Duration.TotalSeconds ?? 0);
            var artist = t.Metadata?.Artist;
            var title = t.Metadata?.DisplayTitle ?? Path.GetFileName(t.FilePath);
            var label = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
            sb.AppendLine($"#EXTINF:{seconds},{label}");
            sb.AppendLine(t.FilePath);
        }
        return sb.ToString();
    }
}
