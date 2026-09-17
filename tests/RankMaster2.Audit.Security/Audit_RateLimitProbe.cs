using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Security;

/// <summary>Throwaway: does the per-address limit on POST /pair actually trip in this host?</summary>
public sealed class Audit_RateLimitProbe(Rm2Server server) : AuditTestBase(server)
{
    [Fact]
    public async Task Eight_rapid_wrong_guesses_report_their_statuses()
    {
        var statuses = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var r = await Anonymous.PairAsync("111111", "probe");
            statuses.Add(r.StatusCode);
        }

        // Written to a file so the audit can quote it; the assertion is deliberately loose.
        await File.WriteAllTextAsync(
            "/tmp/claude-1012/-var-www-bormin/fe4dff48-a725-4cf2-9d43-ad14f7dbf86e/scratchpad/ratelimit-probe.txt",
            string.Join(",", statuses));
        Assert.Contains(429, statuses);
    }
}
