using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Compatibility.Support;

/// <summary>
/// This assembly's own collection definition.
/// <para/>
/// xUnit discovers <c>[CollectionDefinition]</c> only in the test assembly it is running, so the
/// definition in <c>RankMaster2.Server.Tests</c> is invisible here. Without this, a class marked
/// <c>[Collection("rm2-server")]</c> would land in an anonymous collection with no fixture and the
/// <see cref="Rm2Server"/> constructor argument would never be supplied.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuditServerCollection : ICollectionFixture<Rm2Server>
{
    public const string Name = "rm2-audit-compatibility";
}

/// <summary>
/// Base for every test that opens a session. The server holds exactly one session and one folder
/// lock (SERVER_SPEC.md § 7), so a test that leaves one open would cascade into every test after
/// it — hence the close before and after.
/// </summary>
[Collection(AuditServerCollection.Name)]
public abstract class AuditSessionTest(Rm2Server server) : IAsyncLifetime
{
    protected Rm2Server Server { get; } = server;

    protected Task<Rm2Client> ClientAsync() => Server.AuthenticatedAsync();

    public async Task InitializeAsync() => await Server.ResetAsync();

    public async Task DisposeAsync() => await Server.ResetAsync();

    protected async Task<Snapshot> OpenAsync(string folder)
    {
        var client = await ClientAsync();
        var response = await client.OpenSessionAsync(folder);
        return response.ShouldBeSnapshot(201, "POST /session on a rankable folder opens it (SERVER_SPEC.md § 10.1)");
    }

    protected async Task<Snapshot> ReadAsync()
    {
        var client = await ClientAsync();
        var response = await client.GetSessionAsync();
        return response.ShouldBeSnapshot(200, "GET /session returns the live snapshot (SERVER_SPEC.md § 10.2)");
    }

    protected async Task CloseAsync()
    {
        var client = await ClientAsync();
        await client.CloseSessionAsync();
    }
}
