namespace RankMaster2.Server.Tests.Harness;

/// <summary>Finds the contract documents, so tests can assert against them directly.</summary>
public static class Repo
{
    private static readonly Lazy<string> RootPath = new(Locate);

    public static string Root => RootPath.Value;

    public static string ServerSpec => Path.Combine(Root, "SERVER_SPEC.md");
    public static string OpenApi => Path.Combine(Root, "openapi.yaml");

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SERVER_SPEC.md")) &&
                File.Exists(Path.Combine(directory.FullName, "openapi.yaml")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find SERVER_SPEC.md and openapi.yaml above '{AppContext.BaseDirectory}'.");
    }
}
