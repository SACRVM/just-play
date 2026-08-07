using System.Collections.Generic;
using System.Text.Json;
using JustPlay.Core.Models;
using Xunit;

namespace JustPlay.Core.Tests;

/// <summary>
/// Round-trip and backward-compat tests for <see cref="StreamServerProfile"/> and the
/// streaming fields on <see cref="UserSettings"/>. Uses the source-generated
/// <see cref="StreamingJsonContext"/> (trim/AOT-safe path) for profile serialization, and the
/// same reflection-based <see cref="JsonSerializerOptions"/> that <c>JsonSettingsService</c>
/// uses for UserSettings round-trips (matching the existing settings persistence pattern).
/// </summary>
public class StreamServerProfileTests
{
    // Match exactly what JsonSettingsService uses so the tests exercise the real persistence path.
    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // -- StreamServerProfile default values -------------------------------------------

    [Fact]
    public void DefaultValues_AreAsSpecified()
    {
        var profile = new StreamServerProfile();

        Assert.Equal(StreamFormat.Mp3, profile.Format);
        Assert.Equal(320, profile.BitrateKbps);
        Assert.Equal("source", profile.Username);
        Assert.Equal(IcecastProtocol.Put, profile.Protocol);
        Assert.Equal(8000, profile.Port);
        Assert.Equal("/live.mp3", profile.Mount);
        Assert.False(profile.UseTls);
        Assert.NotEmpty(profile.Id); // auto-generated GUID

        // Station info: empty + unlisted by default (a club/private stream stays off the public directory).
        Assert.Equal("", profile.Url);
        Assert.Equal("", profile.Genre);
        Assert.Equal("", profile.Description);
        Assert.False(profile.IsPublic);
    }

    // -- Source-gen context round-trip: single profile --------------------------------

    [Fact]
    public void SingleProfile_RoundTrips_ViaSourceGenContext()
    {
        var profile = new StreamServerProfile
        {
            Id = "abc-123",
            Name = "My 3DX Club",
            Host = "stream.example.com",
            Port = 8000,
            Mount = "/live.mp3",
            Username = "source",
            Password = "s3cr3t",
            Format = StreamFormat.Mp3,
            BitrateKbps = 320,
            UseTls = true,
            Protocol = IcecastProtocol.Put,
            Url = "https://chloe.fm",
            Genre = "Hard Dance",
            Description = "Two classes better",
            IsPublic = true,
        };

        var json = JsonSerializer.Serialize(profile, StreamingJsonContext.Default.StreamServerProfile);
        var restored = JsonSerializer.Deserialize(json, StreamingJsonContext.Default.StreamServerProfile);

        Assert.NotNull(restored);
        Assert.Equal("abc-123", restored!.Id);
        Assert.Equal("My 3DX Club", restored.Name);
        Assert.Equal("stream.example.com", restored.Host);
        Assert.Equal(8000, restored.Port);
        Assert.Equal("/live.mp3", restored.Mount);
        Assert.Equal("source", restored.Username);
        Assert.Equal("s3cr3t", restored.Password);
        Assert.Equal(StreamFormat.Mp3, restored.Format);
        Assert.Equal(320, restored.BitrateKbps);
        Assert.True(restored.UseTls);
        Assert.Equal(IcecastProtocol.Put, restored.Protocol);
        Assert.Equal("https://chloe.fm", restored.Url);
        Assert.Equal("Hard Dance", restored.Genre);
        Assert.Equal("Two classes better", restored.Description);
        Assert.True(restored.IsPublic);
    }

    // -- Source-gen context round-trip: multiple profiles (List) ----------------------

    [Fact]
    public void MultipleProfiles_RoundTrip_ViaSourceGenContext()
    {
        var profiles = new List<StreamServerProfile>
        {
            new()
            {
                Id = "id-1",
                Name = "Test Server",
                Host = "localhost",
                Port = 8000,
                Mount = "/test.mp3",
                Username = "source",
                Password = "pw1",
                Format = StreamFormat.Mp3,
                BitrateKbps = 128,
                UseTls = false,
                Protocol = IcecastProtocol.Put,
            },
            new()
            {
                Id = "id-2",
                Name = "Live Club",
                Host = "stream.club.com",
                Port = 8443,
                Mount = "/live.mp3",
                Username = "source",
                Password = "pw2",
                Format = StreamFormat.Mp3,
                BitrateKbps = 256,
                UseTls = true,
                Protocol = IcecastProtocol.Put,
            },
            new()
            {
                Id = "id-3",
                Name = "Legacy SHOUTcast",
                Host = "old.server.com",
                Port = 8000,
                Mount = "/stream.mp3",
                Username = "source",
                Password = "pw3",
                Format = StreamFormat.Opus,
                BitrateKbps = 96,
                UseTls = false,
                Protocol = IcecastProtocol.Source,
            },
        };

        var json = JsonSerializer.Serialize(profiles, StreamingJsonContext.Default.ListStreamServerProfile);
        var restored = JsonSerializer.Deserialize(json, StreamingJsonContext.Default.ListStreamServerProfile);

        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Count);

        Assert.Equal("id-1", restored[0].Id);
        Assert.Equal("Test Server", restored[0].Name);
        Assert.Equal(128, restored[0].BitrateKbps);
        Assert.Equal(IcecastProtocol.Put, restored[0].Protocol);

        Assert.Equal("id-2", restored[1].Id);
        Assert.Equal("Live Club", restored[1].Name);
        Assert.Equal(256, restored[1].BitrateKbps);
        Assert.True(restored[1].UseTls);

        Assert.Equal("id-3", restored[2].Id);
        Assert.Equal(StreamFormat.Opus, restored[2].Format);
        Assert.Equal(96, restored[2].BitrateKbps);
        Assert.Equal(IcecastProtocol.Source, restored[2].Protocol);
    }

    // -- UserSettings round-trip: streaming fields survive ----------------------------

    [Fact]
    public void UserSettings_WithStreamServers_RoundTrips()
    {
        var settings = new UserSettings
        {
            Theme = "Aurora",
            StreamServers =
            [
                new StreamServerProfile
                {
                    Id = "srv-1",
                    Name = "My Server",
                    Host = "icecast.example.com",
                    Port = 8000,
                    Mount = "/dj.mp3",
                    Username = "source",
                    Password = "mypassword",
                    Format = StreamFormat.Mp3,
                    BitrateKbps = 256,
                    UseTls = false,
                    Protocol = IcecastProtocol.Put,
                },
            ],
            SelectedStreamServerId = "srv-1",
        };

        var json = JsonSerializer.Serialize(settings, SettingsOptions);
        var restored = JsonSerializer.Deserialize<UserSettings>(json, SettingsOptions);

        Assert.NotNull(restored);
        Assert.Single(restored!.StreamServers);
        Assert.Equal("srv-1", restored.StreamServers[0].Id);
        Assert.Equal("My Server", restored.StreamServers[0].Name);
        Assert.Equal(256, restored.StreamServers[0].BitrateKbps);
        Assert.Equal("srv-1", restored.SelectedStreamServerId);
    }

    // -- Backward compat: old settings JSON without streaming fields -------------------

    [Fact]
    public void OldSettingsJson_WithoutStreamingFields_LoadsWithDefaults_NoException()
    {
        // Simulate a settings.json written by a build that predates S1 - no StreamServers,
        // no SelectedStreamServerId. Deserializing it must not throw and must yield safe defaults.
        const string oldJson = """
            {
                "Theme": "Sunset",
                "VinylSpinEnabled": true,
                "WaveformEnabled": false,
                "DefaultTab": "Up Next",
                "AutoAnalyze": false,
                "AnalysisThreads": 2,
                "AutoWriteOnAnalyze": false,
                "WriteDjComment": true
            }
            """;

        var settings = JsonSerializer.Deserialize<UserSettings>(oldJson, SettingsOptions);

        Assert.NotNull(settings);
        Assert.Empty(settings!.StreamServers);              // empty list, not null
        Assert.Null(settings.SelectedStreamServerId);       // null, not an error

        // Existing fields must still load correctly.
        Assert.Equal("Sunset", settings.Theme);
        Assert.False(settings.WaveformEnabled);
        Assert.Equal(2, settings.AnalysisThreads);
        Assert.True(settings.WriteDjComment);
    }

    [Fact]
    public void OldSettingsJson_EmptyObject_LoadsWithDefaults_NoException()
    {
        // Edge case: completely empty JSON object.
        var settings = JsonSerializer.Deserialize<UserSettings>("{}", SettingsOptions);

        Assert.NotNull(settings);
        Assert.Empty(settings!.StreamServers);
        Assert.Null(settings.SelectedStreamServerId);
        Assert.Equal("Aurora", settings.Theme); // UserSettings.Defaults value
    }
}
