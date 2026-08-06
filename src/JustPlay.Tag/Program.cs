using System;
using System.IO;
using Avalonia;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Storage;
using JustPlay.Metadata;
using JustPlay.Tag.ViewModels;
using JustPlay.UI.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Tag;

internal sealed class Program
{
    /// <summary>Composition root — the one place that knows the concrete adapters (TagLib# metadata, theme engine).</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    [STAThread]
    public static void Main(string[] args)
    {
        // Crash safety net (memory: never-crash-error-reporter) — log instead of dying silently.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException (fatal)");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception, "Unobserved background-task exception");
            e.SetObserved();
        };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception, "UI thread");
            e.Handled = true; // keep the app alive
        };

        Services = ConfigureServices(StartupTarget.From(args));

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Avalonia startup / fatal runtime");
        }
    }

    private static IServiceProvider ConfigureServices(StartupTarget startup)
    {
        var services = new ServiceCollection();

        // What we were launched ON, if anything — see StartupTarget.
        services.AddSingleton(startup);

        // All tag I/O goes through JustPlay.Metadata (TagLib#) — the app never touches TagLib directly.
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<IMetadataWriter, TagLibMetadataWriter>();

        // Shared J.U.S.T. live-theme engine (JustPlay.UI) — same palettes/look as JUST PLAY / STREAM.
        services.AddSingleton<IThemeService, AvaloniaThemeService>();

        // Persisted preferences (theme + ID3 write mode) → %LOCALAPPDATA%\JustTag\settings.json.
        services.AddSingleton<Settings.TagSettingsService>();

        // Preview playback — the thing mp3tag cannot do. A tagger only ever needs one stream, so
        // this is the plain engine with no mixer/queue on top.
        services.AddSingleton<IAudioEngine, Audio.Bass.BassAudioEngine>();
        services.AddSingleton<PreviewViewModel>();

        // THE editor is the shared one from JustPlay.UI — JUST TAG docks the same panel + view model
        // that JUST PLAY and the PRE CUE FINDER float. It used to have its own copy; that copy was
        // the divergence (memory suite-unify-dont-patch-divergence) and is gone.
        //
        // The executor is what makes previewing safe: a file that is being played has an open
        // handle, and a tag write on an open handle fails. JUST PLAY defers such a write to the
        // track change because it is performing; a tagger has no reason to wait — it lets go of the
        // file and writes immediately. Same abstraction, the answer each app can actually give.
        services.AddSingleton(sp =>
        {
            var preview = sp.GetRequiredService<PreviewViewModel>();
            return new UI.ViewModels.TagEditorViewModel(
                sp.GetRequiredService<IMetadataReader>(),
                sp.GetRequiredService<IMetadataWriter>(),
                (path, write) =>
                {
                    preview.ReleaseIfPlaying(path);
                    write(path);
                    return UI.ViewModels.TagWriteOutcome.Written;
                });
        });

        services.AddSingleton<TaggerViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void LogCrash(Exception? ex, string context)
    {
        try
        {
            var dir = JustDataPaths.Combine("JustTag");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {context}\n{ex}\n\n");
        }
        catch { /* last-resort: swallow */ }
        Console.Error.WriteLine($"[JUST TAG CRASH] {context}: {ex}");
    }
}
