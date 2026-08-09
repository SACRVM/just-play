using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JustPlay.UI.Theming;
using JustPlay.App.Services;
using JustPlay.App.ViewModels;
using JustPlay.App.Views;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    // "About Just Play" in the macOS app menu - same shared About dialog as the brand mark
    // in the chrome bar (MaxView.OnAboutClick), owned by the main window.
    private void OnAboutMenu(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            var about = new JustPlay.UI.Views.AboutWindow(new JustPlay.UI.Views.AboutInfo(
                AppName: "JustPlay",
                Tagline: "Key-aware DJ music player",
                Version: $"Version {AppInfo.Version}",
                Glyph: BrandGlyphs.Play));
            about.ShowDialog(owner);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the persisted theme BEFORE the main window opens so the user
        // never sees a one-frame Aurora flash before their picked theme lands.
        // Application.Current.Resources is fully populated at this point - the
        // theme service can safely overwrite the colour keys.
        var settings = Program.Services.GetRequiredService<ISettingsService>();
        var themeSvc = Program.Services.GetRequiredService<IThemeService>();
        themeSvc.Apply(Themes.ByNameOrDefault(settings.Current.Theme));

        // The shared row-drag behavior lives in JustPlay.UI (JUST TAG drags files out too), but the
        // crash reporter does not - it owns the Oops dialog, which is this app's. So the app hands it
        // its reporter; an app that has none simply swallows a failed drag, which is the right default.
        JustPlay.UI.Behaviors.RowDragBehavior.OnError = (ex, context) => ErrorReporter.Report(ex, context);

        // Tell the index service which root this machine uses, at STARTUP rather than when the PRE CUE
        // FINDER first opens. Two reasons: HasIndex/Count answer correctly before the finder exists, and
        // UseRoot is what announces an existing index to the suite (LibraryIndexRegistry) - so JUST TAG
        // can find it without JUST PLAY's finder having been opened in this session. It only ever reads;
        // a root that was never scanned has no database file and is not announced.
        Program.Services.GetRequiredService<ILibraryIndexService>()
               .UseRoot(Program.Services.GetRequiredService<IFinderSettingsService>().Current.LibraryRoot);

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

            // Closing JUST PLAY's main window quits the whole app - and takes any independent top-level
            // (the PRE CUE FINDER) down with it (Avalonia's shutdown closes every remaining window, so the
            // finder's OnClosed cleanup still runs + WindowPlacement persists its bounds). Without this the
            // default OnLastWindowClose would leave the app alive whenever the finder is open.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // -- File-open intake (single-instance) ---------------------------
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

            // -- Auto-update (v0.2) -------------------------------------------
            // Begin background release checks for THIS build's version. No-op when the user
            // opted out; surfaces the green title-bar badge when a newer release is found.
            var appVersion = typeof(App).Assembly.GetName().Version ?? new System.Version(0, 0, 0);
            vm.Update.Start(appVersion);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
