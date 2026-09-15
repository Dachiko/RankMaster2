using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Security;

/// <summary>
/// One server for the whole audit assembly, for the same reason the builder's suite shares one:
/// there is exactly one session server-wide (SERVER_SPEC.md § 7), and <c>SessionRegistry.Shared</c>
/// is a process-wide static. Tests in this collection therefore run one at a time.
/// <para/>
/// xUnit discovers collection definitions per assembly, so this redeclares the collection over the
/// builder's <see cref="Rm2Server"/> fixture rather than reusing their definition.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuditServerCollection : ICollectionFixture<Rm2Server>
{
    public const string Name = "rm2-audit-security";
}

[Collection(AuditServerCollection.Name)]
public abstract class AuditTestBase(Rm2Server server) : IAsyncLifetime
{
    protected Rm2Server Server { get; } = server;

    protected Rm2Client Anonymous => Server.Anonymous;

    protected Task<Rm2Client> ClientAsync() => Server.AuthenticatedAsync();

    public async Task InitializeAsync() => await Server.ResetAsync();

    public async Task DisposeAsync() => await Server.ResetAsync();
}
