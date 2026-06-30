using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JustPlay.Core.Abstractions;
using JustPlay.Core.Theming;
using JustPlay.Tag.ViewModels;
using JustPlay.Tag.Views;
using JustPlay.UI.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace JustPlay.Tag;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the persisted theme BEFORE the window opens so the shared design system (JustPlay.UI) has
        // the full palette (incl. the glow/alpha keys with no XAML default) in Application.Resources.
        var settings = Program.Services.GetRequiredService<Settings.TagSettingsService>();
        var themeSvc = Program.Services.GetRequiredService<IThemeService>();
        themeSvc.Apply(Themes.ByNameOrDefault(settings.Current.Theme));

        // Apply the persisted MP3 write mode (ID3v2 version + encoding) to the TagLib# globals up front,
        // so the very first Save uses the user's chosen format (default = mp3tag-compatible v2.3/UTF-16).
        Program.Services.GetRequiredService<IMetadataWriter>().ConfigureId3WriteFormat(settings.WriteFormat);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = Program.Services.GetRequiredService<TagEditorViewModel>(),
            };

            // JUST suite UI philosophy: the brand glyph (the TAG mark) on the active theme gradient as the
            // taskbar / Alt-Tab icon, via the SHARED renderer (JustPlay.UI) — re-rendered on theme switch.
            window.Icon = ThemedWindowIcon.Render(themeSvc.Current, BrandGlyphs.Tag);
            themeSvc.ThemeChanged += (_, theme) =>
                Dispatcher.UIThread.Post(() => window.Icon = ThemedWindowIcon.Render(theme, BrandGlyphs.Tag));

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
