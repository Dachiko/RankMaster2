namespace RankMaster2.Kit.EmptyAvalonia;

using Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
        builder = OperatingSystem.IsWindows()
            ? builder.UseWin32().UseSkia()
            : builder.UseX11().UseSkia();
        return builder.WithInterFont();
    }
}
