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

        Services = ConfigureServices();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Avalonia startup / fatal runtime");
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // All tag I/O goes through JustPlay.Metadata (TagLib#) — the app never touches TagLib directly.
        services.AddSingleton<IMetadataReader, TagLibMetadataReader>();
        services.AddSingleton<IMetadataWriter, TagLibMetadataWriter>();

        // Shared J.U.S.T. live-theme engine (JustPlay.UI) — same palettes/look as JUST PLAY / STREAM.
        services.AddSingleton<IThemeService, AvaloniaThemeService>();

        // Persisted preferences (theme + ID3 write mode) → %LOCALAPPDATA%\JustTag\settings.json.
        services.AddSingleton<Settings.TagSettingsService>();

        services.AddSingleton<TagEditorViewModel>();
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
