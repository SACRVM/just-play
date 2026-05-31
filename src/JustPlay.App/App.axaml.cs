using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JustPlay.App.Theming;
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
            window.Icon = ThemedWindowIcon.Render(themeSvc.Current);
            themeSvc.ThemeChanged += (_, theme) =>
                Dispatcher.UIThread.Post(() => window.Icon = ThemedWindowIcon.Render(theme));

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
