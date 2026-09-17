// The console host. Everything it builds lives in Rm2Host, because RankMaster2.Tray.exe builds the
// same application and the two must never drift apart.
//
// AUDIT2.md § 2.3: this is the host SERVER_RUNNING.md § 1 tells the owner to run when he is
// "diagnosing something" — so a start-up failure landing here as a bare stack trace defeats its own
// purpose. The exception filter below leaves `StopTheHostException` alone: that internal type is how
// `WebApplicationFactory<Program>` (every integration test in this repo) captures the built host
// without actually starting it, and it must keep propagating out of Main exactly as before, or the
// whole test harness breaks.
try
{
    RankMaster2.Server.Rm2Host.Build(args).Run();
}
catch (Exception e) when (e.GetType().Name != "StopTheHostException")
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(RankMaster2.Server.Rm2Host.DescribeStartupFailure(e));
    Console.Error.WriteLine();
    Console.Error.WriteLine("Technical detail (SERVER_RUNNING.md § 12 covers common causes):");
    Console.Error.WriteLine(e);
    Environment.ExitCode = 1;
}

// WebApplicationFactory<Program> in the test projects needs this type to be public and to live in
// the entry-point assembly.
public partial class Program;
