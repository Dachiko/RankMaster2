using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.StateMachine.Harness;

/// <summary>
/// The audit suite runs against its own in-process server, for the same reason the conformance
/// suite does: SERVER_SPEC.md § 7 says exactly one session exists server-wide, so the tests that
/// open one must not run in parallel with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuditServerCollection : ICollectionFixture<Rm2Server>
{
    public const string Name = "rm2-audit-state-machine";
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
