using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Models;
using JustPlay.Core.Theming;
using JustPlay.Stream.Settings;

namespace JustPlay.Stream.ViewModels;

/// <summary>
/// ViewModel for the JUST STREAM settings window (wide-not-tall, horizontal tabs:
/// Server / Audio / Stream / DSP / Advanced — just-stream-blueprint.md §3a/§7.3).
///
/// Server tab: full CRUD over <see cref="StreamViewModel.Profiles"/> via the bindable
/// <see cref="EditableServerProfile"/> wrapper. Audio / Stream / DSP tabs bind directly to the
/// shared <see cref="Stream"/> instance (single source of truth — no duplicated state).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>The live main-window VM. The Audio/Stream/DSP tabs bind to this (e.g. {Binding Stream.EqLow}).</summary>
    public StreamViewModel Stream { get; }

    private readonly IThemeService _themeSvc;
    private readonly JsonStreamSettingsService _settingsSvc;

    // Option sources for the Server/Stream form ComboBoxes.
    public StreamFormat[] Formats { get; } = { StreamFormat.Mp3, StreamFormat.Opus };
    public IcecastProtocol[] Protocols { get; } = { IcecastProtocol.Put, IcecastProtocol.Source };
    public int[] Bitrates { get; } = { 128, 192, 256, 320 };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateServerCommand))]
    private StreamServerProfile? _selectedServer;

    /// <summary>The currently-edited profile, exposed mutably for the Server form. Null when none selected.</summary>
    [ObservableProperty]
    private EditableServerProfile? _editing;

    /// <summary>Active settings tab — drives the shared underline-tab strip (Server | Advanced),
    /// same pattern as JustPlay's tweaks tabs (a string + IsVisible toggles, not a raw TabControl).</summary>
    [ObservableProperty]
    private string _settingsTab = "Server";

    [RelayCommand]
    private void SetSettingsTab(string tab) => SettingsTab = tab;

    private bool _syncingSelection;

    public SettingsViewModel(StreamViewModel stream, IThemeService themeSvc, JsonStreamSettingsService settingsSvc)
    {
        Stream = stream;
        _themeSvc = themeSvc;
        _settingsSvc = settingsSvc;
        _selectedServer = stream.SelectedProfile ?? stream.Profiles.FirstOrDefault();
        LoadEditing();
        // Lock all server CRUD while on air — changing host/mount/creds mid-broadcast is nonsensical.
        Stream.PropertyChanged += OnStreamPropertyChanged;
    }

    /// <summary>False while a broadcast is live — the whole Server tab (list + add/dup/del + form) is
    /// disabled so you can't change connection details out from under an active stream.</summary>
    public bool CanEditServers => !Stream.IsConnected;

    private void OnStreamPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StreamViewModel.IsConnected)) return;
        OnPropertyChanged(nameof(CanEditServers));
        AddServerCommand.NotifyCanExecuteChanged();
        RemoveServerCommand.NotifyCanExecuteChanged();
        DuplicateServerCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Name of the currently active theme — drives the swatch "active" ring in the Look tab.</summary>
    public string CurrentTheme => _settingsSvc.Current.Theme;

    /// <summary>Apply a named theme live and persist the choice (mirrors JUST PLAY's SetThemeCommand pattern).</summary>
    [RelayCommand]
    private void SetTheme(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var theme = Themes.ByNameOrDefault(name);
        _themeSvc.Apply(theme);
        _settingsSvc.Current.Theme = theme.Name;
        _settingsSvc.Save();
        OnPropertyChanged(nameof(CurrentTheme));
    }

    /// <summary>Server profiles — the SAME collection the main window shows.</summary>
    public System.Collections.ObjectModel.ObservableCollection<StreamServerProfile> Servers => Stream.Profiles;

    partial void OnSelectedServerChanged(StreamServerProfile? value)
    {
        if (_syncingSelection) return;
        LoadEditing();
    }

    private void LoadEditing()
    {
        if (Editing is not null)
            Editing.PropertyChanged -= OnEditingChanged;

        Editing = SelectedServer is null ? null : new EditableServerProfile(SelectedServer);

        if (Editing is not null)
            Editing.PropertyChanged += OnEditingChanged;
    }

    /// <summary>Write edits back into the shared collection (replace the immutable record) + persist.</summary>
    private void OnEditingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Editing is null) return;
        var idx = IndexOf(Editing.Id);
        if (idx < 0) return;

        var updated = Editing.ToProfile();

        // Guard the WHOLE mutation. Replacing Servers[idx] makes the ListBox re-evaluate its
        // SelectedItem (the old record is gone) and push a selection change back SYNCHRONOUSLY —
        // which, if the guard were set after the replace, would re-enter OnSelectedServerChanged →
        // LoadEditing and rebuild the editor mid-edit, dropping focus after a single keystroke.
        // We keep the SAME Editing object (Id is stable via ToProfile), so the form bindings survive.
        _syncingSelection = true;
        Servers[idx] = updated;
        SelectedServer = updated;
        if (Stream.SelectedProfile?.Id == updated.Id)
            Stream.SelectedProfile = updated; // keep the main window pointing at the edited profile
        _syncingSelection = false;

        Stream.SaveSettings();
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < Servers.Count; i++)
            if (Servers[i].Id == id) return i;
        return -1;
    }

    [RelayCommand(CanExecute = nameof(CanEditServers))]
    private void AddServer()
    {
        var p = new StreamServerProfile { Name = "New Server" };
        Servers.Add(p);
        SelectedServer = p;
        Stream.SaveSettings();
    }

    // Modify (del/dup) needs a selection AND no live stream.
    private bool CanModifySelected() => SelectedServer is not null && CanEditServers;

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private void RemoveServer()
    {
        if (SelectedServer is null) return;
        var idx = IndexOf(SelectedServer.Id);
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.ElementAtOrDefault(System.Math.Max(0, idx - 1));
        Stream.SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private void DuplicateServer()
    {
        if (SelectedServer is null) return;
        var copy = SelectedServer with { Id = System.Guid.NewGuid().ToString(), Name = SelectedServer.Name + " (copy)" };
        Servers.Add(copy);
        SelectedServer = copy;
        Stream.SaveSettings();
    }
}
