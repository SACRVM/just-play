using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JustPlay.UI.Theming;
using JustPlay.App.ViewModels;
using JustPlay.App.Views;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the persisted theme BEFORE the main window opens so the user
        // never sees a one-frame Aurora flash before their picked theme lands.
        // Application.Current.Resources is fully populated at this point — the
        // theme service can safely overwrite the colour keys.
        var settings = Program.Services.GetRequiredService<ISettingsService>();
        var themeSvc = Program.Services.GetRequiredService<IThemeService>();
        themeSvc.Apply(Themes.ByNameOrDefault(settings.Current.Theme));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = Program.Services.GetRequiredService<MainWindowViewModel>(),
            };

            // Theme-tinted window icon (taskbar / Alt-Tab / title bar): render the
            // brand mark for the active palette now, and re-render on every theme
            // switch. Uses the IThemeService.ThemeChanged we already raise.
            window.Icon = ThemedWindowIcon.Render(themeSvc.Current, BrandGlyphs.Play);
            themeSvc.ThemeChanged += (_, theme) =>
                Dispatcher.UIThread.Post(() => window.Icon = ThemedWindowIcon.Render(theme, BrandGlyphs.Play));

            desktop.MainWindow = window;

            // Closing JUST PLAY's main window quits the whole app — and takes any independent top-level
            // (the PRE CUE FINDER) down with it (Avalonia's shutdown closes every remaining window, so the
            // finder's OnClosed cleanup still runs + WindowPlacement persists its bounds). Without this the
            // default OnLastWindowClose would leave the app alive whenever the finder is open. Chloe 2026-07-07.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // ── File-open intake (single-instance) ───────────────────────────
            // Add any files we were launched with (double-click / file association), and listen
            // for files forwarded by LATER launches while we're already running. Everything is
            // marshalled to the UI thread; a forwarded open also surfaces the window.
            var vm = (MainWindowViewModel)window.DataContext!;
            if (Program.PendingOpen.Files.Count > 0)
            {
                var open = Program.PendingOpen;
                Dispatcher.UIThread.Post(() => _ = vm.OpenIncomingAsync(open.Files, open.AddOnly));
            }
            Program.Single?.StartServer((paths, addOnly) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                    _ = vm.OpenIncomingAsync(paths, addOnly);
                }));

            // ── Auto-update (v0.2) ───────────────────────────────────────────
            // Begin background release checks for THIS build's version. No-op when the user
            // opted out; surfaces the green title-bar badge when a newer release is found.
            var appVersion = typeof(App).Assembly.GetName().Version ?? new System.Version(0, 0, 0);
            vm.Update.Start(appVersion);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
