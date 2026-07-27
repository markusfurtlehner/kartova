using Avalonia;
using Avalonia.Media;

namespace DirStat.App;

internal static class Program
{
    // Must run before any Avalonia type is touched, so it cannot be an instance method.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless work is checked before anything touches Avalonia, so scripting the app on
        // a build agent or over SSH needs no display at all.
        if (Cli.CommandLine.WantsHeadless(args)) return Cli.CommandLine.Run(args);

        if (!TryVerifyDisplay(out var problem))
        {
            Console.Error.WriteLine(problem);
            return 78; // EX_CONFIG
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception e)
        {
            // A GUI toolkit that cannot initialise otherwise dies without ever explaining
            // itself, which over SSH looks like the app simply hanging.
            Console.Error.WriteLine("DirStat failed to start.");
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>
    /// Confirms there is a display server to draw on before Avalonia is initialised.
    /// </summary>
    /// <remarks>
    /// Without this an X11 launch with no <c>DISPLAY</c> — over SSH, in a container, or from
    /// a non-login shell that never sourced the desktop profile — produces a process that
    /// sits there indefinitely showing nothing and reporting nothing.
    /// </remarks>
    private static bool TryVerifyDisplay(out string problem)
    {
        problem = string.Empty;
        if (!OperatingSystem.IsLinux()) return true;

        var x11 = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!string.IsNullOrEmpty(x11) || !string.IsNullOrEmpty(wayland)) return true;

        problem =
            """
            DirStat found no display server: neither DISPLAY nor WAYLAND_DISPLAY is set.

            DirStat is a graphical application and needs a desktop session.

              - Over SSH, reconnect with X11 forwarding:  ssh -X user@host
              - Under WSL, run it from a login shell so WSLg sets the environment,
                or export it yourself:  export DISPLAY=:0
              - In a container, pass the display through along with /tmp/.X11-unix
            """;
        return false;
    }

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

                // The native file dialog on Linux is the XDG desktop portal, reached over the
                // D-Bus session bus. Where there is no session bus — WSLg, minimal containers,
                // bare X11 sessions — that call never returns and the picker simply never
                // appears. Falling back to Avalonia's own dialog keeps "choose a folder"
                // working everywhere, which matters more here than native integration.
                UseDBusFilePicker = HasSessionBus(),
            })
            .LogToTrace();

    /// <summary>True when a D-Bus session bus is reachable, so portal calls can be answered.</summary>
    private static bool HasSessionBus()
    {
        if (!OperatingSystem.IsLinux()) return false;

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            return true;

        // With no address exported, the well-known socket is the other place it can live.
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtimeDir) && File.Exists(Path.Combine(runtimeDir, "bus"));
    }
}
