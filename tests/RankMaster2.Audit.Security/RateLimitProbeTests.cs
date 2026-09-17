using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Security;

/// <summary>SERVER_SPEC.md § 15: the per-address limit on POST /pair actually trips in this host.</summary>
public sealed class RateLimitProbeTests(Rm2Server server) : AuditTestBase(server)
{
    [Fact]
    public async Task Eight_rapid_wrong_guesses_include_a_429()
    {
        var statuses = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var r = await Anonymous.PairAsync("111111", "probe");
            statuses.Add(r.StatusCode);
        }

        Assert.Contains(429, statuses);
    }
}
