using CommunityToolkit.Mvvm.ComponentModel;
using JustPlay.Core.Models;

namespace JustPlay.Stream.ViewModels;

/// <summary>
/// Mutable, bindable editor wrapper around the immutable <see cref="StreamServerProfile"/> record,
/// so the settings form can two-way bind to its fields. <see cref="ToProfile"/> converts back to the
/// record for persistence / the broadcast service.
/// </summary>
public sealed partial class EditableServerProfile : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _host;
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _mount;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _password;
    [ObservableProperty] private StreamFormat _format;
    [ObservableProperty] private int _bitrateKbps;
    [ObservableProperty] private bool _useTls;
    [ObservableProperty] private IcecastProtocol _protocol;

    // Station info (ICY directory fields - optional, DJ-owned).
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _genre;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _isPublic;

    public EditableServerProfile(StreamServerProfile p)
    {
        Id = p.Id;
        _name = p.Name;
        _host = p.Host;
        _port = p.Port;
        _mount = p.Mount;
        _username = p.Username;
        _password = p.Password;
        _format = p.Format;
        _bitrateKbps = p.BitrateKbps;
        _useTls = p.UseTls;
        _protocol = p.Protocol;
        _url = p.Url;
        _genre = p.Genre;
        _description = p.Description;
        _isPublic = p.IsPublic;
    }

    public StreamServerProfile ToProfile() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        Mount = Mount,
        Username = Username,
        Password = Password,
        Format = Format,
        BitrateKbps = BitrateKbps,
        UseTls = UseTls,
        Protocol = Protocol,
        Url = Url,
        Genre = Genre,
        Description = Description,
        IsPublic = IsPublic,
    };
}
