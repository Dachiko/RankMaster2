using System.Net;
using System.Text.Json;
using RankMaster2.Server.Media;
using RankMaster2.Server.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// `media_decode_failed` says *which* kind of failure it was, in `details.reason`.
///
/// <para>The owner saw "This phone cannot open this file." on an ordinary photograph. Two things
/// were wrong with that sentence and only one of them was the phone's wording: the server had told
/// it nothing it was allowed to act on. The distinction between "these bytes are not an image" and
/// "I had no decode memory free just then" lived only in <c>error.message</c>, which § 4 fixes as
/// unstable and never parsed — so a client obeying the contract could only branch on the code, and
/// both cases share one code. This pins the machine-readable half.</para>
/// </summary>
public sealed class DecodeFailureReasonTests(ITestOutputHelper output)
{
    private const int Source = 3000;
    private const int TargetWidth = 1080;

    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").Clone();
    }

    [Fact]
    public async Task A_picture_refused_for_want_of_memory_says_no_room_and_never_calls_itself_damaged()
    {
        // A ceiling below one decode's peak, so the refusal is certain rather than raced.
        var big = PngWriter.Rgb(Source, Source, (x, y) => ((byte)(x % 256), (byte)(y % 256), (byte)80));

        using var session = new StubSession();
        session.Add("holiday.png", big);

        var options = new MediaOptions
        {
            DecodeMemoryLimitMegabytes = 16,
            MaxConcurrentDecodes = 2,
            CacheDirectory = Path.Combine(Path.GetTempPath(), "rm2-reason-" + Guid.NewGuid().ToString("N")[..8]),
        };

        await using var host = new MediaHost(session, options);
        var response = await host.Client.GetAsync($"/api/v1/media/holiday.png/still?w={TargetWidth}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var error = await ErrorOf(response);
        output.WriteLine(error.ToString());

        Assert.Equal("media_decode_failed", error.GetProperty("code").GetString());
        Assert.Equal("no_room", error.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal("holiday.png", error.GetProperty("details").GetProperty("id").GetString());

        // The prose must actively exonerate the file, not merely avoid accusing it — a client that
        // passes the message straight through is showing this to him.
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("not damaged", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_picture_that_really_is_not_an_image_says_unreadable()
    {
        using var session = new StubSession();
        session.Add("notes.jpg", "this is plain text, not a picture"u8.ToArray());

        var options = new MediaOptions
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "rm2-reason-" + Guid.NewGuid().ToString("N")[..8]),
        };

        await using var host = new MediaHost(session, options);
        var response = await host.Client.GetAsync($"/api/v1/media/notes.jpg/still?w={TargetWidth}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var error = await ErrorOf(response);
        output.WriteLine(error.ToString());

        Assert.Equal("media_decode_failed", error.GetProperty("code").GetString());
        Assert.Equal("unreadable", error.GetProperty("details").GetProperty("reason").GetString());
    }
}
