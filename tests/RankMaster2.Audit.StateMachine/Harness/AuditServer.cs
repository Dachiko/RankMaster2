using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.StateMachine.Harness;

/// <summary>
/// The audit suite runs against its own in-process server, for the same reason the conformance
/// suite does: SERVER_SPEC.md § 7 says exactly one session exists server-wide, so the tests that
/// open one must not run in parallel with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuditServerCollection :
    ICollectionFixture<Rm2Server>,
    ICollectionFixture<SaveOnEveryChoiceServer>,
    ICollectionFixture<CountBoundServer>
{
    public const string Name = "rm2-audit-state-machine";
}

/// <summary>
/// Which side of SERVER_SPEC.md § 13.1's switch a row of the § 8.3 table is being asserted on.
/// Since the write-behind, "the write failed" reaches a client by two different routes, and the
/// table's rows are the same at the end of both — which is the claim these modes exist to check.
/// </summary>
public enum SaveMode
{
    /// <summary><c>SaveDelaySeconds = 0</c>: the write is inside the choice, as it always was.</summary>
    SaveOnEveryChoice,

    /// <summary>The default: the write is deferred, bounded, and its failure is latched.</summary>
    WriteBehind,
}

/// <summary>
/// Base class for an audit test that opens a session. Closes whatever the previous test left
/// behind, so one failure does not strand the folder lock and cascade into everything after it.
/// </summary>
[Collection(AuditServerCollection.Name)]
public abstract class AuditTestBase(Rm2Server server) : IAsyncLifetime
{
    protected Rm2Server Server { get; } = server;

    protected Task<Rm2Client> ClientAsync() => Server.AuthenticatedAsync();

    public async Task InitializeAsync() => await Server.ResetAsync();

    public async Task DisposeAsync() => await Server.ResetAsync();

    protected async Task<Snapshot> OpenAsync(string folder)
    {
        var client = await ClientAsync();
        var response = await client.OpenSessionAsync(folder);
        return response.ShouldBeSnapshot(
            201, "POST /session on a rankable folder opens it (SERVER_SPEC.md § 10.1)");
    }

    protected Task<Snapshot> OpenAsync(AuditFolder folder) => OpenAsync(folder.Path);
}
