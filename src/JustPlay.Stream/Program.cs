using System;
using System.IO;
using Avalonia;
using JustPlay.Audio.Bass;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Audio;
using JustPlay.Core.Storage;
using JustPlay.Stream.Settings;
using JustPlay.Stream.ViewModels;
using JustPlay.UI.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Stream;

internal sealed class Program
{
    /// <summary>Composition root - the one place that knows the concrete BASS backends.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    [STAThread]
    public static void Main(string[] args)
    {
        // Crash safety net (memory: never-crash-error-reporter) - log instead of dying silently.
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

        // Per-process "capture a specific APP" provider (Phase 0, Path A). Windows gets the real
        // WASAPI process-loopback capturer; macOS 14.4+ the Core Audio process-tap capturer
        // (native shim, see research/macos-app-audio-capture.md); everything else the Null
        // provider (the "App" source is then hidden). Injected into the capture engine below.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IProcessAudioCapture, WasapiProcessLoopbackCapture>();
        else if (OperatingSystem.IsMacOSVersionAtLeast(14, 4))
            services.AddSingleton<IProcessAudioCapture, MacProcessAudioCapture>();
        else
            services.AddSingleton<IProcessAudioCapture, NullProcessAudioCapture>();

        // Capture engine: registered as IAudioInputEngine AND IBassMixerSource so the SAME instance
        // backs both the UI and the broadcast service (the encoder taps its post-DSP mixer).
        services.AddSingleton<BassInputCaptureEngine>();
        services.AddSingleton<IAudioInputEngine>(sp => sp.GetRequiredService<BassInputCaptureEngine>());
        services.AddSingleton<IBassMixerSource>(sp => sp.GetRequiredService<BassInputCaptureEngine>());
        services.AddSingleton<IBroadcastService, BassBroadcastService>();
        // "Record your set": a SECOND, independent encoder on the same post-DSP mixer the
        // broadcast taps - its failure (disk full etc.) can never touch the stream.
        services.AddSingleton<IRecordingService, BassRecordingService>();

        services.AddSingleton<JsonStreamSettingsService>();
        // Persists the event log to a daily session file (7-day retention); pruning runs in its ctor.
        // Shared SessionLog (JustPlay.Core), parameterised with this app's LocalAppData folder.
        services.AddSingleton<JustPlay.Core.Logging.ISessionLog>(_ => new JustPlay.Core.Logging.SessionLog("JustStream"));

        // Shared J.U.S.T. live-theme engine (JustPlay.UI) - same palettes as JUST PLAY, applied at
        // startup so the design system + brand icon track the active theme (and can switch live).
        services.AddSingleton<IThemeService, AvaloniaThemeService>();

        services.AddSingleton<StreamViewModel>();
        services.AddTransient<SettingsViewModel>();

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
            var dir = JustDataPaths.Combine("JustStream");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {context}\n{ex}\n\n");
        }
        catch { /* last-resort: swallow */ }
        Console.Error.WriteLine($"[JUST STREAM CRASH] {context}: {ex}");
    }
}
