using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Theming;
using JustPlay.Library;
using JustPlay.Tag.Settings;

namespace JustPlay.Tag.ViewModels;

/// <summary>
/// JUST TAG settings - theme picker + ID3 write mode. A separate window (like JUST STREAM's) because the
/// theme switcher couldn't size reliably at the editor's bottom edge. Singleton so it
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

    /// <summary>Active theme name - drives the swatch "active" ring (via StringEqualsConverter).</summary>
    [ObservableProperty] private string _currentTheme;

    /// <summary>Active ID3 write mode - drives the write-format option highlight (via EnumEqualsConverter).</summary>
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

    /// <summary>
    /// How many files the file table reads at once - JUST PLAY's "Analysis threads" control, ported
    /// rather than re-invented, so one knob does not grow two dialects across the suite. Reading a
    /// tag waits on the NETWORK, not on a core, which is why raising it shortens a big folder almost
    /// linearly. Clamped to the suite's one ceiling (AnalysisBatchOptions.MaxSupportedConcurrency).
    ///
    /// <para>This view model WRITES it; TaggerViewModel reads it back off the settings service when
    /// it starts a pass, so a change takes effect on the next folder without any wiring between the
    /// two.</para>
    /// </summary>
    public int Threads => Math.Clamp(_settings.Current.Threads, 1, AnalysisBatchOptions.MaxSupportedConcurrency);

    /// <summary>String form, so the radio buttons drive their active state through the same
    /// StringEqualsConverter the theme swatches use.</summary>
    public string ThreadsText => Threads.ToString(CultureInfo.InvariantCulture);

    [RelayCommand]
    private void SetThreads(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return;

        var clamped = Math.Clamp(n, 1, AnalysisBatchOptions.MaxSupportedConcurrency);
        if (clamped == Threads) return;

        _settings.Current.Threads = clamped;
        _settings.Save();
        OnPropertyChanged(nameof(Threads));
        OnPropertyChanged(nameof(ThreadsText));
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
