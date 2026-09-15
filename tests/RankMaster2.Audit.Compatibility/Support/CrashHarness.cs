using System.Diagnostics;
using System.Text;

namespace RankMaster2.Audit.Compatibility.Support;

/// <summary>
/// Runs <c>JsonCatalog.Save</c> in a saveable loop in a <b>separate process</b> and kills it with
/// SIGKILL at an unpredictable moment.
/// <para/>
/// It has to be a separate process. SPEC.md § Persistence promises "write tmp, Flush(true),
/// File.Replace", and the only thing that promise is worth anything against is a process that dies
/// without unwinding — no finally block, no flush, no cleanup. Faking it in-process would test the
/// test. The child is compiled on demand against the very assemblies the suite loaded, so it runs
/// the same <c>Save</c> the server runs.
/// </summary>
public static class CrashHarness
{
    public sealed record Outcome(bool Ran, string Reason, int Trials, int LeftoverTempFiles);

    private static readonly Lazy<string?> Dotnet = new(FindDotnet);
    private static readonly Lazy<(string? Exe, string Diagnostic)> Child = new(BuildChild);

    public static bool Available => Child.Value.Exe is not null;
    public static string Diagnostic => Child.Value.Diagnostic;

    /// <summary>
    /// Starts the saver on <paramref name="folder"/>, waits for it to report that it is inside the
    /// loop, then kills it after <paramref name="delay"/>. Returns once the process is gone.
    /// </summary>
    public static void KillDuringSave(string folder, TimeSpan delay)
    {
        var exe = Child.Value.Exe
            ?? throw new InvalidOperationException("The crash child was not built: " + Child.Value.Diagnostic);

        var start = new ProcessStartInfo(Dotnet.Value!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(exe);
        start.ArgumentList.Add(folder);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the crash child.");

        try
        {
            var ready = process.StandardOutput.ReadLine();
            if (ready is null)
            {
                var error = process.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    "The crash child exited before it began saving.\n" + error);
            }

            Thread.Sleep(delay);

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);   // SIGKILL on Unix: no unwinding, no cleanup.
        }
        finally
        {
            if (!process.WaitForExit(15_000) && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    // ---- building the child -------------------------------------------------------------------

    private static (string? Exe, string Diagnostic) BuildChild()
    {
        if (Dotnet.Value is null)
            return (null, "No `dotnet` executable could be found, so the out-of-process kill cannot run.");

        var bin = AppContext.BaseDirectory;
        var needed = new[] { "RankMaster2.Core.dll", "RankMaster2.Ranking.dll", "RankMaster2.Catalog.dll" };
        foreach (var name in needed)
        {
            if (!File.Exists(Path.Combine(bin, name)))
                return (null, $"{name} is not next to the test assembly ({bin}).");
        }

        var work = Path.Combine(Path.GetTempPath(), "rm2-audit", "crash-child");
        Directory.CreateDirectory(work);

        var references = string.Join("\n", needed.Select(name =>
            $"""    <Reference Include="{Path.GetFileNameWithoutExtension(name)}"><HintPath>{Path.Combine(bin, name)}</HintPath></Reference>"""));

        File.WriteAllText(Path.Combine(work, "crashsaver.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>crashsaver</AssemblyName>
                <RootNamespace>CrashSaver</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
            {references}
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(work, "Program.cs"), ChildSource);

        var start = new ProcessStartInfo(Dotnet.Value)
        {
            WorkingDirectory = work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Debug");
        start.ArgumentList.Add("--nologo");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";

        using var build = Process.Start(start)!;
        var output = new StringBuilder();
        output.Append(build.StandardOutput.ReadToEnd());
        output.Append(build.StandardError.ReadToEnd());
        build.WaitForExit(180_000);

        var exe = Path.Combine(work, "bin", "Debug", "net8.0", "crashsaver.dll");
        if (build.ExitCode != 0 || !File.Exists(exe))
            return (null, $"Building the crash child failed (exit {build.ExitCode}):\n{output}");

        return (exe, "built at " + exe);
    }

    private static string? FindDotnet()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet"),
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

        return null;
    }

    /// <summary>
    /// The child: scan, then save in a tight loop with a changed rating every time, so a kill at
    /// any moment has a real chance of landing between the temp write and the replace.
    /// </summary>
    private const string ChildSource = """
        using RankMaster2;
        using RankMaster2.Catalog;

        var folder = args[0];
        var catalog = new JsonCatalog();
        var records = catalog.Scan(folder).ToList();
        catalog.Save(folder, records);

        Console.Out.WriteLine("READY");
        Console.Out.Flush();

        var mu = 25.0;
        while (true)
        {
            mu += 0.000001;
            for (var i = 0; i < records.Count; i++)
                records[i] = records[i] with { Rating = new Rating(mu + i, records[i].Rating.Sigma) };

            catalog.Save(folder, records);
        }
        """;
}
