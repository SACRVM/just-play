using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using JustPlay.Core.Models;

namespace JustPlay.App.ViewModels;

/// <summary>
/// UI wrapper around a <see cref="Track"/>. Metadata and analysis arrive asynchronously;
/// call <see cref="Refresh"/> when the underlying model gains data so bindings update.
/// </summary>
public sealed partial class TrackViewModel : ObservableObject
{
    private Bitmap? _cover;
    private bool _coverResolved;

    public TrackViewModel(Track model) => Model = model;

    public Track Model { get; }

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>1-based position in the current Tracks list (assigned by the shell VM).</summary>
    [ObservableProperty]
    private int _index;

    public string Title =>
        Model.Metadata?.DisplayTitle ?? Path.GetFileNameWithoutExtension(Model.FilePath);

    public string Artist =>
        string.IsNullOrWhiteSpace(Model.Metadata?.Artist) ? "—" : Model.Metadata!.Artist!;

    public string DurationText =>
        Model.Metadata is { Duration: var d } && d > TimeSpan.Zero
            ? d.ToString(@"m\:ss")
            : "–:––";

    /// <summary>Analysed BPM if we have it, else whatever the tags claimed.</summary>
    public string BpmText
    {
        get
        {
            var bpm = Model.Analysis?.Bpm ?? Model.Metadata?.TaggedBpm;
            return bpm is > 0 ? bpm.Value.ToString("0") : "";
        }
    }

    public string KeyText =>
        Model.Analysis?.Key?.Camelot ?? Model.Metadata?.TaggedKey ?? "";

    public string EnergyText =>
        Model.Analysis?.Energy is int e ? e.ToString() : "";

    public int? Energy => Model.Analysis?.Energy;

    public Bitmap? Cover
    {
        get
        {
            if (_coverResolved) return _cover;
            _coverResolved = true;
            var data = Model.Metadata?.CoverArt;
            if (data is { Length: > 0 })
            {
                try
                {
                    _cover = new Bitmap(new MemoryStream(data));
                    Console.WriteLine($"[Cover OK] {Path.GetFileName(Model.FilePath)} → {data.Length} bytes");
                }
                catch (Exception ex)
                {
                    _cover = null;
                    Console.WriteLine($"[Cover FAIL] {Path.GetFileName(Model.FilePath)} → {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[Cover NONE] {Path.GetFileName(Model.FilePath)} → no embedded picture");
            }
            return _cover;
        }
    }

    /// <summary>Re-evaluate all derived properties after the model gains metadata/analysis.</summary>
    public void Refresh()
    {
        _coverResolved = false;
        OnPropertyChanged(string.Empty); // refresh every binding on this object
    }
}
