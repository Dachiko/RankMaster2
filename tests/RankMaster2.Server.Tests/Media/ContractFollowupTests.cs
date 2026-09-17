using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// Media questions the independent audit raised that turned out to be the contract working as
/// written, kept here so the answers stay answered.
///
/// <para>One of its cases did not survive. <c>Audit_416_headers</c> asserted that a
/// <c>416 range_not_satisfiable</c> carries no <c>ETag</c> — the server does send one, and nothing in
/// SERVER_SPEC.md § 12.4 or RFC 9110 says it should not, so the assertion was the test's own opinion
/// rather than the contract's. What § 12.4 does require of a 416 — <c>Content-Range: bytes */size</c>
/// and the error envelope — is asserted in <c>MediaTests</c>, and was already passing.</para>
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public sealed class ContractFollowupTests(Rm2Server server, ITestOutputHelper output) : SessionTestBase(server)
{
    /// <summary>
    /// SERVER_SPEC.md § 12.3: <c>HEAD</c> is the same response without the body, which means a cold
    /// still is rendered to answer it — the <c>Content-Length</c> is the real one, not a guess and
    /// not a 404 because the cache was empty. A client that HEADs before it GETs must not be told a
    /// different story.
    /// </summary>
    [Fact]
    public async Task Head_on_a_cold_still_renders_it_and_reports_the_real_length()
    {
        using var folder = LibraryFolder.SixStills();
        var opened = await OpenAsync(folder);
        var client = await ClientAsync();

        var head = await client.HeadAsync(opened.Left.StillLink!);
        output.WriteLine($"HEAD cold still -> {head.StatusCode} Content-Length={head.HeaderOrNull("Content-Length")}");

        head.ShouldHaveStatus(200, "SERVER_SPEC.md § 12.3: HEAD is the GET without the body, cache cold or not");

        var length = head.HeaderOrNull("Content-Length");
        Assert.True(long.TryParse(length, out var bytes) && bytes > 0,
            $"HEAD must report the length it would have sent; got '{length ?? "(absent)"}'.");

        var get = await client.GetAsync(opened.Left.StillLink!);
        get.ShouldHaveStatus(200, "and the GET that follows agrees with it");
        Assert.Equal(bytes, get.Body.LongLength);
        Assert.Equal(head.HeaderOrNull("ETag"), get.HeaderOrNull("ETag"));
    }
}
