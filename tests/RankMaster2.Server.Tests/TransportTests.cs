using System.Net.Http.Headers;
using System.Text;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// SERVER_SPEC.md § 2 and § 4: the headers on every response, the one error envelope, and the
/// request shapes the server must refuse. These apply to routes nobody has written yet as much as
/// to the ones that exist — "including routing 404s and unhandled exceptions" is the whole point of
/// having a single envelope.
/// </summary>
[Collection(Rm2ServerCollection.Name)]
public class TransportTests(Rm2Server server) : SessionTestBase(server)
{
    [Fact]
    public async Task Every_response_carries_a_request_id_and_nosniff()
    {
        var response = await Anonymous.PingAsync(authenticate: false);

        Assert.True(response.RequestId is { Length: > 0 },
            "SERVER_SPEC.md § 2: every response MUST carry X-Request-Id. It is the key the server logs " +
            "the detail of a 500 under, so without it a client's bug report is unusable.");

        var nosniff = response.HeaderOrNull("X-Content-Type-Options");
        Assert.True(string.Equals(nosniff, "nosniff", StringComparison.OrdinalIgnoreCase),
            $"SERVER_SPEC.md § 2: every response MUST carry X-Content-Type-Options: nosniff. Got '{nosniff ?? "(absent)"}'.");
    }

    [Fact]
    public async Task Request_ids_are_unique_per_request()
    {
        var first = await Anonymous.PingAsync(authenticate: false);
        var second = await Anonymous.PingAsync(authenticate: false);

        Assert.True(first.RequestId != second.RequestId,
            $"SERVER_SPEC.md § 2: X-Request-Id is 'opaque, unique per request'. Two pings both returned " +
            $"'{first.RequestId}'.");
    }

    [Fact]
    public async Task An_unknown_route_is_a_not_found_in_the_standard_envelope()
    {
        var client = await ClientAsync();
        var response = await client.GetAsync("/no/such/route");

        response.ShouldBeError("not_found",
            "SERVER_SPEC.md § 4 and § 5.2: an unknown route is 404 not_found, in the same envelope as " +
            "everything else. There is no second error shape.");
    }

    /// <summary>
    /// An unknown route reached without a token is the one place the contract points two ways:
    /// § 3 puts "everything else" behind a bearer token, while § 5.2 and § 6 give "no such route"
    /// as 404 `not_found` without qualifying it. Both answers are defensible — answering 401 first
    /// also avoids telling an unauthenticated caller which routes exist — so this asserts only what
    /// both readings agree on: whichever it is, it arrives in the standard envelope.
    /// </summary>
    [Fact]
    public async Task An_unknown_route_without_a_token_still_uses_the_envelope()
    {
        var response = await Anonymous.GetAsync("/no/such/route", authenticate: false);
        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 4: every non-2xx response with a body uses the one error envelope",
            "not_found", "unauthenticated");
    }

    /// <summary>
    /// SERVER_SPEC.md § 1.1 used to forbid a rename endpoint outright; that product decision is
    /// reversed (2026-09-16) and `/session/rename` is now real (SERVER_SPEC.md § 10.16, covered by
    /// <c>RenameTests</c>). The rule that stands is the general one: an unknown path containing
    /// "rename" still falls through to the ordinary 404, and this pins exactly that — not the one
    /// reversed route.
    /// </summary>
    [Theory]
    [InlineData("/rename")]
    [InlineData("/library/rename-by-rank")]
    [InlineData("/debug/rename")]
    public async Task There_is_no_rename_route(string path)
    {
        var get = await Anonymous.GetAsync(path, authenticate: false);
        Assert.True(get.StatusCode is 404 or 401 or 405,
            $"SERVER_SPEC.md § 1.1: GET {path} must not be a route. Got {get.StatusCode}.\n{get.Describe()}");

        var post = await Anonymous.SendAsync(HttpMethod.Post, path, new { }, authenticate: false);
        Assert.True(post.StatusCode is 404 or 401 or 405,
            $"SERVER_SPEC.md § 1.1: POST {path} must not be a route. Got {post.StatusCode}.\n{post.Describe()}");
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused_by_content_type()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", null, customise: request =>
        {
            request.Content = new StringContent("folder=/tmp", Encoding.UTF8, "application/x-www-form-urlencoded");
        });

        response.ShouldBeError("unsupported_content_type",
            "SERVER_SPEC.md § 2: a JSON request body must be application/json; anything else is 415.");
    }

    [Fact]
    public async Task A_body_over_sixty_four_kilobytes_is_refused()
    {
        var client = await ClientAsync();

        // 65536 is the cap (SERVER_SPEC.md § 15); this clears it by a comfortable margin.
        var padding = new string('x', 70_000);
        var oversized = $"{{\"folder\":\"/tmp\",\"padding\":\"{padding}\"}}";

        var response = await client.SendAsync(HttpMethod.Post, "/session", oversized);
        var failure = response.ShouldBeError("payload_too_large",
            "SERVER_SPEC.md § 2 and § 15: a JSON body over 65536 bytes is 413 payload_too_large.");

        Assert.Equal(65536, failure.Detail("maxBytes", "payload_too_large details").GetInt32());
    }

    [Fact]
    public async Task Unknown_json_fields_are_ignored_rather_than_rejected()
    {
        using var folder = Fixtures.LibraryFolder.SixStills();
        var client = await ClientAsync();

        // § 2: "Unknown JSON fields | ignored; the server MUST NOT reject a request for them."
        // This is what lets a newer client talk to an older server without a version handshake.
        var response = await client.SendAsync(HttpMethod.Post, "/session",
            new { folder = folder.Path, somethingThisServerHasNeverHeardOf = 42, nested = new { a = 1 } });

        response.ShouldBeSnapshot(201,
            "SERVER_SPEC.md § 2: unknown JSON fields are ignored, never a reason to reject a request");
    }

    [Fact]
    public async Task A_malformed_json_body_is_an_invalid_request()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{ \"folder\": ");

        response.ShouldBeError("invalid_request",
            "SERVER_SPEC.md § 5.2: a body that is not valid JSON is 400 invalid_request.");
    }

    [Fact]
    public async Task A_missing_required_field_names_the_field()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", new { });

        var failure = response.ShouldBeError("missing_field",
            "SERVER_SPEC.md § 5.2: a required field that is absent or null is 400 missing_field.");

        Assert.Equal("folder", failure.Detail("field", "missing_field details").GetString());
    }

    [Fact]
    public async Task A_field_of_the_wrong_type_is_an_invalid_request()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{ \"folder\": 17 }");

        response.ShouldBeErrorOneOf(
            "SERVER_SPEC.md § 5.2: a field with the wrong JSON type is 400 invalid_request",
            "invalid_request", "invalid_path");
    }

    [Fact]
    public async Task Head_on_a_media_endpoint_mirrors_get_without_a_body()
    {
        using var folder = Fixtures.LibraryFolder.SixStills();
        var snapshot = await OpenAsync(folder);
        var client = await ClientAsync();

        var id = snapshot.Left.Id;
        var path = $"/media/{Rm2Client.EncodeId(id)}/meta";

        var get = await client.GetAsync(path);
        var head = await client.HeadAsync(path);

        // § 2: HEAD "MUST return the identical status line and headers as GET with no body".
        Assert.True(head.StatusCode == get.StatusCode,
            $"SERVER_SPEC.md § 2: HEAD and GET must agree on status. GET was {get.StatusCode}, HEAD was " +
            $"{head.StatusCode}.\n{head.Describe()}");

        foreach (var name in new[] { "Content-Type", "ETag", "Cache-Control" })
        {
            var fromGet = get.HeaderOrNull(name);
            var fromHead = head.HeaderOrNull(name);
            Assert.True(fromGet == fromHead,
                $"SERVER_SPEC.md § 2: HEAD returns the identical headers to GET. {name} was " +
                $"'{fromGet ?? "(absent)"}' on GET and '{fromHead ?? "(absent)"}' on HEAD.");
        }

        // The body is deliberately not asserted here. Suppressing it for a HEAD is the HTTP layer's
        // job, and the in-process test host does not do HTTP framing, so an empty body here would
        // prove nothing and a full one would accuse the server of a fault it does not have.
        // rm2ctl checks this over a real socket, where the answer means something.
    }
}
