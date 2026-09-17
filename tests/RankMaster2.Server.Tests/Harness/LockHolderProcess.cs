using System.Diagnostics;

namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// Holds <c>&lt;folder&gt;/.rankmaster.lock</c> from a <b>second process</b>
/// (<c>tests/RankMaster2.LockHolder</c>), which is the only way to ask the operating system the
/// question SERVER_SPEC.md § 10.1 step 4 is about: can another Rank Master be kept out of a folder
/// this server has open?
///
/// <para>The child prints <c>held</c> once it has the lock, and lets go when its standard input is
/// closed. Waiting for that line rather than sleeping is what makes the test deterministic.</para>
/// </summary>
public sealed class LockHolderProcess : IDisposable
{
    private readonly Process _process;
    private bool _released;

    private LockHolderProcess(Process process) => _process = process;

    public static LockHolderProcess Hold(string folder)
    {
        var dll = Locate();

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(folder);

        var process = Process.Start(start)
            ?? throw new Xunit.Sdk.XunitException($"Could not start the lock holder: dotnet {dll}");

        var holder = new LockHolderProcess(process);

        var readyTask = process.StandardOutput.ReadLineAsync();
        if (!readyTask.Wait(TimeSpan.FromSeconds(30)))
        {
            holder.Dispose();
            throw new Xunit.Sdk.XunitException(
                $"The lock holder did not report within 30 s (dotnet {dll} {folder}).");
        }

        var ready = readyTask.Result;
        if (ready != "held")
        {
            var error = process.StandardError.ReadToEnd();
            holder.Dispose();
            throw new Xunit.Sdk.XunitException(
                $"The lock holder could not take {folder}/.rankmaster.lock: {ready ?? "(no output)"}\n{error}");
        }

        return holder;
    }

    /// <summary>Lets the lock go and waits for the child to exit, so the next open really is racing nothing.</summary>
    public void Release()
    {
        if (_released)
            return;
        _released = true;

        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(10_000))
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Release();
        _process.Dispose();
    }

    /// <summary>
    /// The lock holder is referenced with <c>ReferenceOutputAssembly="false"</c>, so it is built but
    /// not copied beside the tests. Its output sits in the usual place under the repository.
    /// </summary>
    private static string Locate()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "Release"
            : "Debug";

        var candidates = new[] { configuration, "Release", "Debug" }
            .Distinct()
            .Select(c => Path.Combine(
                Repo.Root, "tests", "RankMaster2.LockHolder", "bin", c, "net8.0", "RankMaster2.LockHolder.dll"))
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new Xunit.Sdk.XunitException(
            "RankMaster2.LockHolder.dll was not found. The folder-lock test needs a second process to " +
            "hold the lock (G-audit-remediation § 2, T1b); building RankMaster2.Server.slnf builds it.\n" +
            "  Looked in:\n    " + string.Join("\n    ", candidates));
    }
}
