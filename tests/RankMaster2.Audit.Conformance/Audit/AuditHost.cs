using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Conformance.Audit;

/// <summary>
/// The conformance audit borrows the other suite's <em>plumbing</em> — spinning a host, obtaining a
/// bearer token, building temp folders — and nothing else. Every expectation in this project is read
/// straight out of SERVER_SPEC.md and openapi.yaml. Reusing another agent's assertions would only
/// reproduce their reading of the contract, which is exactly what a second opinion must not do.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuditCollection : ICollectionFixture<Rm2Server>
{
    public const string Name = "rm2-conformance-audit";
}

[Collection(AuditCollection.Name)]
public abstract class AuditTestBase(Rm2Server server) : IAsyncLifetime
{
    protected Rm2Server Server { get; } = server;

    protected Rm2Client Anonymous => Server.Anonymous;

    protected Task<Rm2Client> ClientAsync() => Server.AuthenticatedAsync();

    public async Task InitializeAsync() => await Server.ResetAsync();

    public async Task DisposeAsync() => await Server.ResetAsync();

    /// <summary>
    /// Open a session and hand back the parsed body without asserting anything about its shape —
    /// the shape is what other tests are for, and a setup step that asserts hides its own failures
    /// inside unrelated tests.
    /// </summary>
    protected async Task<Rm2Response> OpenAsync(string folder)
    {
        var client = await ClientAsync();
        return await client.OpenSessionAsync(folder);
    }
}
