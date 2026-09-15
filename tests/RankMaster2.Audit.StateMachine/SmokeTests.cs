using RankMaster2.Audit.StateMachine.Harness;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.StateMachine;

public sealed class SmokeTests(Rm2Server server) : AuditTestBase(server)
{
    [Fact]
    public async Task Harness_opens_a_session_and_the_save_jam_works()
    {
        using var folder = AuditFolder.SixStills();
        var snapshot = await OpenAsync(folder);
        Assert.Equal("ranking", snapshot.State);

        var client = await ClientAsync();
        (await client.SaveAsync()).ShouldBeSnapshot(200, "a clean save succeeds");

        folder.JamSave();
        var jammed = await client.SaveAsync();
        jammed.ShouldBeError("save_failed", "a jammed save must fail rather than silently succeed");

        folder.UnjamSave();
        (await client.SaveAsync()).ShouldBeSnapshot(200, "unjamming restores the save");
    }
}
