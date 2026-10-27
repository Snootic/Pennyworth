using Avalonia;
using Pennyworth.App.Services.Windows;

namespace Pennyworth.App;

internal static class Program
{
    // Don't use Avalonia types before AppMain runs: nothing is initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        // The short-lived elevated copy started by "Unlock full scan": measure, write, exit. No window.
        if (OperatingSystem.IsWindows() && ElevatedHiddenSpaceService.TryRunHelper(args, out int exitCode))
        {
            return exitCode;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>().UsePlatformDetect();
#if WAYLAND
        // Inside a Wayland session talk to the compositor itself (decision D6) instead of going through XWayland.
        // UsePlatformDetect never picks that backend; the fallback keeps X11 when no compositor answers.
        // The app id is the desktop file's name, which is how compositors find the launcher icon.
        if (OperatingSystem.IsLinux() && !KeepX11())
        {
            builder = builder.With(new WaylandPlatformOptions { AppId = "pennyworth" }).UseWaylandWithFallback();
        }
#endif
        return builder;
    }

#if WAYLAND
    /// <summary>
    /// <c>PENNYWORTH_DISPLAY=x11</c> keeps X11 (XWayland) inside a Wayland session, which is also how a test run on a
    /// Wayland desktop gets the X server it was pointed at.
    /// </summary>
    private static bool KeepX11() =>
        string.Equals(Environment.GetEnvironmentVariable("PENNYWORTH_DISPLAY"), "x11", StringComparison.OrdinalIgnoreCase);
#endif
}
