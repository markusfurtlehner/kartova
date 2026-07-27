using Avalonia;
using Avalonia.Media;

namespace DirStat.App;

internal static class Program
{
    // Must run before any Avalonia type is touched, so it cannot be an instance method.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                // Inter ships inside the binary, so text renders identically on a bare
                // Linux container and on a fully provisioned desktop.
                DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter",
            })
            .With(new Win32PlatformOptions
            {
                // ANGLE gives a consistent GPU path across the wide range of drivers
                // found on Windows; software rendering remains the automatic fallback.
                RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            })
            .With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Software],
                EnableIme = true,
            })
            .LogToTrace();
}
