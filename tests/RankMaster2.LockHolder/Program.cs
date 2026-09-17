// A second process that holds <folder>/.rankmaster.lock exactly as another Rank Master would, so
// SessionLifecycleTests can ask the operating system the question instead of asking the server's own
// process (G-audit-remediation § 2, T1b).
//
// The old test took the lock from inside the test process. On .NET that is enforced in-process as
// well, so it passed — and proved nothing about whether a *different* program can be kept out, which
// is the entire claim of SERVER_SPEC.md § 10.1 step 4. It even carried an escape hatch saying "if
// this platform's locks are advisory the server cannot detect the conflict", which turned a real
// failure into a message. On Linux .NET maps FileShare.None onto flock(2), which is cross-process,
// so the 423 is real here too and the hatch was never needed.
//
// Usage: RankMaster2.LockHolder <folder>
//   Prints "held" on stdout once the lock is taken (or "failed: <reason>" if it could not be), then
//   blocks on stdin. Closing the child's stdin releases the lock and exits 0. The handshake is what
//   makes the test deterministic: the parent waits for that line rather than sleeping.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: RankMaster2.LockHolder <folder>");
    return 2;
}

var path = Path.Combine(args[0], ".rankmaster.lock");

FileStream held;
try
{
    held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (Exception e)
{
    Console.Out.WriteLine("failed: " + e.Message);
    Console.Out.Flush();
    return 1;
}

using (held)
{
    Console.Out.WriteLine("held");
    Console.Out.Flush();

    // Blocks until the parent closes our stdin, which is how it says "you may let go".
    Console.In.ReadLine();
}

return 0;
