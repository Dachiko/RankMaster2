namespace RankMaster2.Pc.App;

using Avalonia;

/// <summary>A-startup-and-shell.md § 3.1. The entry point: one special-case mode
/// (<c>--build-vlc-cache</c>, § 4.3) that runs and exits before anything else, then the ordinary
/// path — clock, crash handler, Avalonia — with nothing native but Avalonia's own touched before
/// the first frame.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--build-vlc-cache"))
            return RunBuildVlcCache();

        StartupClock.Start(AppInfo.Version);
        CrashLog.Install();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static int RunBuildVlcCache()
    {
        var nativeDir = LibVlcLayout.NativeDir ?? Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        var result = LibVlcIndex.Build(nativeDir);
        Console.WriteLine(result.Message);
        return result.ExitCode;
    }

    // Referenced by the Avalonia designer and by headless tests (Avalonia.Headless.XUnit needs a
    // configured AppBuilder to build a test app from).
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
        builder = OperatingSystem.IsWindows()
            ? builder.UseWin32().UseSkia()
            : builder.UseX11().UseSkia();
        return builder.WithInterFont();
    }
}
