using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;
using JustPlay.Stream.Settings;
using JustPlay.Stream.ViewModels;
using JustPlay.Stream.Views;
using JustPlay.UI.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Stream;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // "About Just Stream" in the macOS app menu - same shared About dialog as the Funkturm
    // brand mark in the chrome (MainWindow.OnAbout), owned by the main window.
    private void OnAboutMenu(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            var asm = typeof(App).Assembly;
            var info = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
            var ver = info?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "";
            var about = new JustPlay.UI.Views.AboutWindow(new JustPlay.UI.Views.AboutInfo(
                AppName: "JUST STREAM",
                Tagline: "Broadcast streaming console",
                Version: string.IsNullOrEmpty(ver) ? "" : $"Version {ver}",
                Glyph: BrandGlyphs.RadioTower));
            about.ShowDialog(owner);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the saved theme BEFORE the window opens - this publishes the full palette (incl.
        // the glow/alpha keys that have no XAML default) into Application.Resources, so the shared
        // design system renders correctly and JUST STREAM matches JUST PLAY. Falls back to Aurora when
        // the settings file is missing or the saved name is stale (Themes.ByNameOrDefault).
        var themeSvc = Program.Services.GetRequiredService<IThemeService>();
        var settings = Program.Services.GetRequiredService<JsonStreamSettingsService>();
        themeSvc.Apply(Themes.ByNameOrDefault(settings.Current.Theme));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = Program.Services.GetRequiredService<StreamViewModel>(),
            };

            // Schema F: the Funkturm brand mark on the active theme gradient as the taskbar /
            // Alt-Tab icon, via the SHARED renderer (JustPlay.UI) - re-rendered on every theme switch.
            window.Icon = ThemedWindowIcon.Render(themeSvc.Current, BrandGlyphs.RadioTower);
            themeSvc.ThemeChanged += (_, theme) =>
                Dispatcher.UIThread.Post(() => window.Icon = ThemedWindowIcon.Render(theme, BrandGlyphs.RadioTower));

            desktop.MainWindow = window;

            // Dispose the VM on shutdown - it finalizes a running set recording (EncodeStop
            // completes the WAV/AIFF/FLAC headers; dying mid-write leaves an unplayable file).
            desktop.Exit += (_, _) => (window.DataContext as StreamViewModel)?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
