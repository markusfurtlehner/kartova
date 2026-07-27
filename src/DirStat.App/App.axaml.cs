using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DirStat.App.Services;
using DirStat.App.ViewModels;
using DirStat.App.Views;

namespace DirStat.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsService.Load();

            // Follow the operating system on first run rather than assuming English.
            Localization.Loc.Current.Language =
                Enum.TryParse<Localization.AppLanguage>(settings.Language, out var saved)
                    ? saved
                    : Localization.Loc.DetectSystemLanguage();

            RequestedThemeVariant = settings.Theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.System => ThemeVariant.Default,
                _ => ThemeVariant.Dark,
            };

            var viewModel = new MainViewModel(settings);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            desktop.ShutdownRequested += (_, _) => viewModel.PersistSettings();

            // Command line: "DirStat C:\Some\Folder" scans immediately.
            var args = desktop.Args ?? [];
            if (args.Length > 0) viewModel.ScanFromCommandLine(args);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
