using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Theming;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// JUST TAG — single-file tag editor. Reads a file's editorial tags + cover via
/// <see cref="IMetadataReader"/>, lets the user edit them, and writes back via
/// <see cref="IMetadataWriter.WriteEditable"/> (analysis fields are never touched).
/// File pickers + drag-drop live in the view (they need the TopLevel) and call
/// <see cref="LoadFile"/> / <see cref="SetNewCover"/>.
/// </summary>
public sealed partial class TagEditorViewModel : ObservableObject
{
    private readonly IMetadataReader _reader;
    private readonly IMetadataWriter _writer;
    private readonly IThemeService _theme;
    private readonly Settings.TagSettingsService _settings;

    // Cover state carried until Save: which action to apply + the bytes/mime for a Replace.
    private byte[]? _coverBytes;
    private string? _coverMime;
    private CoverAction _coverAction = CoverAction.Keep;

    // ID3v2 major version of the loaded file (null = no leading ID3v2 tag / not an MP3).
    private int? _fileId3Major;

    public TagEditorViewModel(IMetadataReader reader, IMetadataWriter writer, IThemeService theme,
        Settings.TagSettingsService settings)
    {
        _reader = reader;
        _writer = writer;
        _theme = theme;
        _settings = settings;
        _currentTheme = theme.Current.Name;

        // Both are singletons that live as long as the app — no unsubscribe needed.
        _settings.WriteFormatChanged += (_, _) => RefreshVersionNotice();
    }

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _fileInfo = "";
    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private string? _album;
    [ObservableProperty] private string? _albumArtist;
    [ObservableProperty] private string? _genre;
    [ObservableProperty] private string? _year;
    [ObservableProperty] private string? _track;
    [ObservableProperty] private string? _comment;
    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private string _status = "Open an audio file to edit its tags.";
    [ObservableProperty] private string _currentTheme;

    /// <summary>
    /// Set when saving this file would ALSO change its ID3v2 version, because the WRITE FORMAT
    /// setting force-normalises every MP3 JUST TAG saves. Null the rest of the time — the notice
    /// only shows up on the files it actually applies to.
    /// </summary>
    [ObservableProperty] private string? _versionNotice;

    /// <summary>The way out of <see cref="VersionNotice"/>; null when no setting avoids it.</summary>
    [ObservableProperty] private string? _versionNoticeFix;

    public bool HasFile => FilePath is not null;
    public bool HasCover => Cover is not null;
    public bool HasVersionNotice => VersionNotice is not null;
    public bool HasVersionNoticeFix => VersionNoticeFix is not null;

    partial void OnVersionNoticeChanged(string? value) => OnPropertyChanged(nameof(HasVersionNotice));
    partial void OnVersionNoticeFixChanged(string? value) => OnPropertyChanged(nameof(HasVersionNoticeFix));

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFile));
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCover));

    // Year / track # are number-only (Chloe) — strip any non-digit the user types or pastes.
    partial void OnYearChanged(string? value)
    {
        var d = Digits(value);
        if (d != value) Year = d;
    }

    partial void OnTrackChanged(string? value)
    {
        var d = Digits(value);
        if (d != value) Track = d;
    }

    private static string? Digits(string? s)
        => s is null ? null : new string(s.Where(char.IsDigit).ToArray());

    /// <summary>Open a file (called by the view's picker / drag-drop) and load its tags.</summary>
    public void LoadFile(string path)
    {
        try
        {
            var m = _reader.Read(path);
            FilePath = path;
            FileName = Path.GetFileName(path);
            Title = m.Title;
            Artist = m.Artist;
            Album = m.Album;
            AlbumArtist = m.AlbumArtist;
            Genre = m.Genre;
            Year = m.Year is > 0 ? m.Year.Value.ToString(CultureInfo.InvariantCulture) : null;
            Track = m.TrackNumber is > 0 ? m.TrackNumber.Value.ToString(CultureInfo.InvariantCulture) : null;
            Comment = m.Comment;
            SetCoverBytes(m.CoverArt, GuessMime(m.CoverArt));
            _coverAction = CoverAction.Keep;
            FileInfo = BuildInfo(m, path);
            _fileId3Major = Settings.Id3VersionProbe.Read(path);
            RefreshVersionNotice();
            Status = $"Loaded · {FileName}";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't read this file — {ex.Message}";
        }
    }

    /// <summary>
    /// Re-evaluate the ID3-version notice for the loaded file against the current WRITE FORMAT.
    /// In one of the three converting modes <c>ForceDefaultVersion = true</c>, so a save converts any
    /// MP3 whose version differs — in BOTH directions, including v2.3 · UTF-16 pulling a v2.4 file
    /// down. That conversion re-encodes the GEOB descriptors Serato and Mixed In Key search by.
    /// In the default KeepFileVersion mode there is no target version and no notice.
    /// </summary>
    private void RefreshVersionNotice()
    {
        if (FilePath is null
            || Settings.Id3VersionProbe.MajorFor(_settings.WriteFormat) is not { } target
            || _fileId3Major is not { } have
            || have == target)
        {
            VersionNotice = null;
            VersionNoticeFix = null;
            return;
        }

        VersionNotice = $"This file is {Settings.Id3VersionProbe.Name(have)}. " +
                        $"Saving converts it to {Settings.Id3VersionProbe.Name(target)}.";

        // The escape hatch is the same one for every file, including an ID3v2.2 that no write format
        // could reproduce: stop converting altogether.
        VersionNoticeFix = "Keep it as it is: choose \"Keep each file's version\" under WRITE FORMAT in Settings.";
    }

    /// <summary>Replace the cover with a picked image (bytes + mime from the view).</summary>
    public void SetNewCover(byte[] bytes, string mime)
    {
        SetCoverBytes(bytes, mime);
        _coverAction = CoverAction.Replace;
        Status = "Cover replaced — not saved yet";
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void RemoveCover()
    {
        SetCoverBytes(null, null);
        _coverAction = CoverAction.Remove;
        Status = "Cover removed — not saved yet";
    }

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void Save()
    {
        if (FilePath is not { } path) return;
        try
        {
            var tags = new EditableTags
            {
                Title = Blank(Title),
                Artist = Blank(Artist),
                Album = Blank(Album),
                AlbumArtist = Blank(AlbumArtist),
                Genre = Blank(Genre),
                Year = ParseUint(Year),
                TrackNumber = ParseUint(Track),
                Comment = Blank(Comment),
            };

            _writer.WriteEditable(path, tags, _coverAction,
                _coverAction == CoverAction.Replace ? _coverBytes : null,
                _coverAction == CoverAction.Replace ? _coverMime : null);

            _coverAction = CoverAction.Keep; // the saved cover is now the file's cover
            // The file now carries whatever version the save produced — re-probe so the notice
            // reflects the file on disk instead of lingering as a warning about something done.
            _fileId3Major = Settings.Id3VersionProbe.Read(path);
            RefreshVersionNotice();
            Status = $"Saved ✓ · {FileName}";
        }
        catch (Exception ex)
        {
            Status = $"Save failed — {ex.Message}";
        }
    }

    [RelayCommand]
    private void SetTheme(string name)
    {
        _theme.Apply(Themes.ByNameOrDefault(name));
        CurrentTheme = _theme.Current.Name;
    }

    private void SetCoverBytes(byte[]? bytes, string? mime)
    {
        _coverBytes = bytes;
        _coverMime = mime;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                Cover = new Bitmap(ms);
                return;
            }
            catch { /* undecodable image bytes — show no cover rather than crash */ }
        }
        Cover = null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static uint ParseUint(string? s) =>
        uint.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0u;

    /// <summary>Sniff the cover MIME from the magic bytes (PNG vs JPEG); default JPEG.</summary>
    private static string? GuessMime(byte[]? b)
    {
        if (b is null || b.Length < 4) return null;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        return "image/jpeg";
    }

    private static string BuildInfo(TrackMetadata m, string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var parts = new System.Collections.Generic.List<string>();
        if (ext.Length > 0) parts.Add(ext);
        if (m.Duration > TimeSpan.Zero) parts.Add(m.Duration.ToString(m.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
        if (m.Bitrate is > 0) parts.Add($"{m.Bitrate} kbps");
        if (m.SampleRate is > 0) parts.Add($"{m.SampleRate / 1000.0:0.#} kHz");
        return string.Join("  ·  ", parts);
    }
}
