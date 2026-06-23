using Avalonia.Controls;
using Avalonia.Interactivity;
using JustPlay.Stream.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Stream.Views;

public partial class MainWindow : Window
{
    private SettingsWindow? _settings;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_settings is { } w)
        {
            w.Activate();
            return;
        }
        _settings = new SettingsWindow
        {
            DataContext = Program.Services.GetRequiredService<SettingsViewModel>(),
        };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show(this);
    }
}
