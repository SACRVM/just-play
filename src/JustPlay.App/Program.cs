using System;
using Avalonia;
using JustPlay.Analysis;
using JustPlay.App.Settings;
using JustPlay.App.Theming;
using JustPlay.App.ViewModels;
using JustPlay.Audio.Bass;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Playback;
using JustPlay.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App;

sealed class Program
{
    /// <summary>Composition root — the one place that knows the concrete backends.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    [STAThread]
    public static void Main(string[] args)
    {
        Services = ConfigureServices();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Backends — swapped here (and only here) when going cross-platform.
        services.AddSingleton<IAudioEngine, BassAudioEngine>();
        services.AddSingleton<IAudioDecoder, BassAudioDecoder>();
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();

        // Analysis stack — BPM detector + the orchestrator that fans tracks
        // out to all registered detectors. Singletons so the BASS_FX side has
        // a single initialised instance for the process lifetime.
        services.AddSingleton<IBpmDetector, BassBpmDetector>();
        services.AddSingleton<ITrackAnalysisService, TrackAnalysisService>();

        // Theming + user preferences. SettingsService reads settings.json
        // from LocalAppData on construction; the ThemeService applies a
        // palette by writing to Application.Current.Resources. Both are
        // singletons so MainWindowViewModel and App.OnFrameworkInitialization
        // can share the same instance.
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IThemeService, AvaloniaThemeService>();

        // Core logic.
        services.AddSingleton<PlaybackController>();

        // ViewModels.
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    // Parameterless — used by Main and by the Avalonia visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
