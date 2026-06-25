using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;
using JustPlay.Stream.ViewModels;
using JustPlay.Stream.Views;
using JustPlay.UI.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Stream;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the active theme BEFORE the window opens — this publishes the full palette (incl.
        // the glow/alpha keys that have no XAML default) into Application.Resources, so the shared
        // design system renders correctly and JUST STREAM matches JUST PLAY. Default Aurora for now;
        // wiring a persisted choice + an in-app switcher is the remaining step.
        var themeSvc = Program.Services.GetRequiredService<IThemeService>();
        themeSvc.Apply(Themes.Aurora);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = Program.Services.GetRequiredService<StreamViewModel>(),
            };

            // Schema F: the Funkturm brand mark on the active theme gradient as the taskbar /
            // Alt-Tab icon, via the SHARED renderer (JustPlay.UI) — re-rendered on every theme switch.
            window.Icon = ThemedWindowIcon.Render(themeSvc.Current, BrandGlyphs.RadioTower);
            themeSvc.ThemeChanged += (_, theme) =>
                Dispatcher.UIThread.Post(() => window.Icon = ThemedWindowIcon.Render(theme, BrandGlyphs.RadioTower));

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
