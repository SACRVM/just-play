using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Theming;
using JustPlay.Tag.Settings;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// JUST TAG settings — theme picker + ID3 write mode. A separate window (like JUST STREAM's) because the
/// theme switcher couldn't size reliably at the editor's bottom edge (Chloe 2026-06-30). Singleton so it
/// reflects + persists the one shared <see cref="IThemeService"/> / <see cref="TagSettingsService"/> state.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IThemeService _theme;
    private readonly IMetadataWriter _writer;
    private readonly TagSettingsService _settings;

    public SettingsViewModel(IThemeService theme, IMetadataWriter writer, TagSettingsService settings)
    {
        _theme = theme;
        _writer = writer;
        _settings = settings;
        _currentTheme = _theme.Current.Name;
        _writeFormat = _settings.WriteFormat;
    }

    /// <summary>Active theme name — drives the swatch "active" ring (via StringEqualsConverter).</summary>
    [ObservableProperty] private string _currentTheme;

    /// <summary>Active ID3 write mode — drives the write-format option highlight (via EnumEqualsConverter).</summary>
    [ObservableProperty] private Id3WriteFormat _writeFormat;

    /// <summary>Apply a named theme live + persist (same pattern as JUST PLAY / STREAM SetThemeCommand).</summary>
    [RelayCommand]
    private void SetTheme(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var theme = Themes.ByNameOrDefault(name);
        _theme.Apply(theme);
        CurrentTheme = theme.Name;
        _settings.Current.Theme = theme.Name;
        _settings.Save();
    }

    /// <summary>Switch the MP3 write mode live + persist; applies the TagLib# global immediately.</summary>
    [RelayCommand]
    private void SetWriteFormat(string? name)
    {
        if (!Enum.TryParse<Id3WriteFormat>(name, out var fmt)) return;
        WriteFormat = fmt;
        _writer.ConfigureId3WriteFormat(fmt);
        // Persist THROUGH the service (not by poking Current) so it can tell the editor to
        // re-check the loaded file against the new format while this window is still open.
        _settings.SetWriteFormat(fmt);
    }
}
