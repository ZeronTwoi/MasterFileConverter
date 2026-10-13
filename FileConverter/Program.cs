using Avalonia;
using FileConverter.Core;
using FileConverter.Platform;

namespace FileConverter;

internal static class Program
{
    /// <summary>
    /// On Windows the app ships a second, console-mode launcher for scripts (its name ends in
    /// "-cli"); started through it, everything is a command line.
    /// </summary>
    private static bool StartedAsCommandLine =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").EndsWith("-cli", StringComparison.OrdinalIgnoreCase);

    // No Avalonia or SynchronizationContext-reliant code before the app is started.
    [STAThread]
    public static int Main(string[] args)
    {
        EnsureCurrentDirectory();

        // a newer version, started by the one it replaces: it only moves in, then leaves
        if (args.Length > 0 && args[0] == AppUpdater.ApplyCommand)
            return AppUpdater.ApplyStaged(HostPlatform.Create(), args);

        Engines.Initialize(HostPlatform.Create(), SelfCommand());

        if (StartedAsCommandLine || CliRunner.Handles(args))
            return CliRunner.Run(args);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (!HasDisplay)
        {
            // Linux without an X display: a server, an SSH session, or a Wayland session without XWayland
            Console.Error.WriteLine(AppInfo.DisplayName + " could not open a window: " + ex.Message);
            Console.Error.WriteLine("The window needs an X display (on Wayland: XWayland). " +
                                    "Without a desktop, use the command line — see --help.");
            return 1;
        }
    }

    /// <summary>
    /// How this program was started, so that it can start copies of itself: its own launcher —
    /// or, when it runs as "dotnet &lt;the app&gt;.dll", that pair.
    /// </summary>
    private static string[]? SelfCommand()
    {
        if (Environment.ProcessPath is not { Length: > 0 } process) return null;
        if (!Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return [process];
        return typeof(Program).Assembly.Location is { Length: > 0 } assembly ? [process, assembly] : null;
    }

    /// <summary>
    /// A program can be started from a folder that has since been deleted (a terminal left open in
    /// it). Libraries that ask for the current folder at start-up then fail outright, so the app
    /// moves to its own folder first.
    /// </summary>
    private static void EnsureCurrentDirectory()
    {
        try
        {
            if (Directory.Exists(Directory.GetCurrentDirectory())) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no current folder at all
        }
        try { Directory.SetCurrentDirectory(AppContext.BaseDirectory); } catch { /* nothing better to offer */ }
    }

    /// <summary>Windows always has a display; on Linux the window is drawn through X11.</summary>
    private static bool HasDisplay =>
        OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
#if FC_WINDOWS
        .UseWin32()
#else
        .UseX11()
        // what a desktop matches a window to its menu entry by (StartupWMClass in the .desktop file)
        .With(new X11PlatformOptions { WmClass = AppInfo.ProgramName })
        .WithInterFont()
#endif
        .UseSkia()
        .UseHarfBuzz()
        .LogToTrace();
}
