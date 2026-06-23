using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using JustPlay.Stream.ViewModels;
using JustPlay.Stream.Views;
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
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
