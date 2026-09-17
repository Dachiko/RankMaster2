using System.Reflection;

namespace RankMaster2;

// Copied from src/RankMaster2.App/AppInfo.cs unchanged (A-startup-and-shell.md § 2): its Version
// goes to part E's start screen exactly as the old app's did.
internal static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus < 0 ? info : info[..plus];
        }

        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
