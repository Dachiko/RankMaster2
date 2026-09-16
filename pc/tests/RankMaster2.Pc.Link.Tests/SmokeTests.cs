using RankMaster2.Pc.Link.Tests.Harness;
using Xunit;

namespace RankMaster2.Pc.Link.Tests;

[Collection("RealServer")]
public sealed class SmokeTests(RealServer server)
{
    [Fact]
    public async Task ServerStartsAndPublishesAnOffer()
    {
        Assert.NotEmpty(server.Fingerprint);
        Assert.True(server.Port > 0);

        var device = await server.PairSecondDeviceAsync("smoke-phone");
        using var http = device.CreateClient();
        var response = await http.GetAsync(server.BaseUrl + "/ping");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task LinkConnectsColdAndEnrols()
    {
        var (link, tap) = LinkFactory.Build(server);
        await using var linkScope = link;

        var result = await link.ConnectAsync();

        Assert.IsType<RankMaster2.Pc.Link.ConnectResult.Connected>(result);
        Assert.NotEmpty(tap.Sent);
    }
}
