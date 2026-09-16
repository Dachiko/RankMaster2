namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>Finds the repository root, so the harness can locate and build the real server.
/// Mirrors <c>tests/RankMaster2.Server.Tests/Harness/Repo.cs</c>, which walks up to
/// <c>SERVER_SPEC.md</c>/<c>openapi.yaml</c>; § 6.2 asks for <c>RankMaster2.sln</c> instead, which
/// is present at the same root and equally unambiguous.</summary>
internal static class Repo
{
    private static readonly Lazy<string> RootPath = new(Locate);

    public static string Root => RootPath.Value;

    public static string ServerProjectDirectory => Path.Combine(Root, "src", "RankMaster2.Server");

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RankMaster2.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find RankMaster2.sln above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>Finds a <c>dotnet</c> executable, the same way
    /// <c>tests/RankMaster2.Audit.Compatibility/Support/CrashHarness.cs</c> does.</summary>
    public static string FindDotnet()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            CombineIfSet(Environment.GetEnvironmentVariable("DOTNET_ROOT"), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet"),
            "/usr/share/dotnet/dotnet",
            "/usr/lib/dotnet/dotnet",
            "/usr/local/share/dotnet/dotnet",
        };

        if (Environment.ProcessPath is { } self &&
            Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            candidates.Insert(0, self);

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("No `dotnet` executable could be found.");
    }

    private static string? CombineIfSet(string? directory, string file) =>
        string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, file);
}
