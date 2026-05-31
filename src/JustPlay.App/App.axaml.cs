using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            desktop.MainWindow = new MainWindow
            {
                DataContext = Program.Services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
