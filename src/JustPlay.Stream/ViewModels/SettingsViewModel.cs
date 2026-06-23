using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustPlay.Core.Models;

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

    public SettingsViewModel(StreamViewModel stream)
    {
        Stream = stream;
        _selectedServer = stream.SelectedProfile ?? stream.Profiles.FirstOrDefault();
        LoadEditing();
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
        Servers[idx] = updated;

        // Keep the main window's selection pointing at the edited profile.
        if (Stream.SelectedProfile?.Id == updated.Id)
            Stream.SelectedProfile = updated;

        // Re-point our own selection at the replaced record without re-running LoadEditing
        // (that would rebuild the editor and drop focus mid-keystroke).
        _syncingSelection = true;
        SelectedServer = updated;
        _syncingSelection = false;

        Stream.SaveSettings();
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < Servers.Count; i++)
            if (Servers[i].Id == id) return i;
        return -1;
    }

    [RelayCommand]
    private void AddServer()
    {
        var p = new StreamServerProfile { Name = "New Server" };
        Servers.Add(p);
        SelectedServer = p;
        Stream.SaveSettings();
    }

    private bool HasSelection() => SelectedServer is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveServer()
    {
        if (SelectedServer is null) return;
        var idx = IndexOf(SelectedServer.Id);
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.ElementAtOrDefault(System.Math.Max(0, idx - 1));
        Stream.SaveSettings();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateServer()
    {
        if (SelectedServer is null) return;
        var copy = SelectedServer with { Id = System.Guid.NewGuid().ToString(), Name = SelectedServer.Name + " (copy)" };
        Servers.Add(copy);
        SelectedServer = copy;
        Stream.SaveSettings();
    }
}
