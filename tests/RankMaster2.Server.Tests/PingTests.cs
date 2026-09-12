using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace RankMaster2.Server.Tests;

public class PingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Ping_Answers()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/ping");
        response.EnsureSuccessStatusCode();
        Assert.Contains("Rank Master 2", await response.Content.ReadAsStringAsync());
    }
}
