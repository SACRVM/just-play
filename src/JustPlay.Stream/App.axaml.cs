using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Program.Services.GetRequiredService<StreamViewModel>(),
                // Schema F: the brand mark (Funkturm on the theme-gradient chip) as the taskbar /
                // Alt-Tab icon, via the SHARED renderer (JustPlay.UI). Phase 2 will drive the live
                // theme set; for now the suite's default Aurora palette.
                Icon = ThemedWindowIcon.Render(Themes.Aurora, BrandGlyphs.RadioTower),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
