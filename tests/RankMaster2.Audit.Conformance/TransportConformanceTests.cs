using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RankMaster2.Audit.Conformance.Audit;
using RankMaster2.Server.Tests.Fixtures;
using RankMaster2.Server.Tests.Harness;
using Xunit;

namespace RankMaster2.Audit.Conformance;

/// <summary>SERVER_SPEC.md § 2 — transport: headers, content types, body limits, HEAD.</summary>
public sealed class TransportConformanceTests(Rm2Server server) : AuditTestBase(server)
{
    private const string EveryResponse =
        "SERVER_SPEC.md § 2: \"Every response MUST carry X-Request-Id and X-Content-Type-Options: nosniff\"";

    // ---- § 2, the two mandatory headers ---------------------------------------------------------

    [Fact]
    public async Task EveryResponseCarriesRequestIdAndNosniff()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var probes = new List<Rm2Response>
        {
            await Anonymous.PingAsync(authenticate: false),
            await client.PingAsync(),
            await client.GetSessionAsync(),                                 // 404 no_session
            await client.GetAsync("/no-such-route"),                        // 404 not_found
            await Anonymous.GetSessionAsync(),                              // 401
            await client.RootsAsync(),
            await client.OpenSessionAsync(folder.Path),                     // 201
            await client.GetSessionAsync(),                                 // 200
            await client.SaveAsync(),                                       // 200
            await client.MetaAsync(MediaFixtures.PlainJpeg),                // 200 JSON
            await client.StillAsync(MediaFixtures.PlainJpeg),               // 200 bytes
            await client.ThumbAsync(MediaFixtures.PlainJpeg),               // 200 bytes
            await client.MetaAsync("does-not-exist.jpg"),                   // 404
            await client.CloseSessionAsync()                                // 204
        };

        var noRequestId = probes.Where(p => string.IsNullOrEmpty(p.RequestId)).ToArray();
        Assert.True(noRequestId.Length == 0,
            EveryResponse + ", and these carry none:\n" +
            string.Join("\n", noRequestId.Select(p => $"  {p.Method} {p.Url} → {p.StatusCode}")));

        var noSniff = probes.Where(p =>
            !string.Equals(p.HeaderOrNull("X-Content-Type-Options"), "nosniff", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(noSniff.Length == 0,
            EveryResponse + ", and these do not:\n" +
            string.Join("\n", noSniff.Select(p =>
                $"  {p.Method} {p.Url} → {p.StatusCode}, X-Content-Type-Options: {p.HeaderOrNull("X-Content-Type-Options") ?? "(absent)"}")));
    }

    [Fact]
    public async Task RequestIdIsUniquePerRequest()
    {
        var client = await ClientAsync();
        var ids = new List<string?>();
        for (var i = 0; i < 5; i++) ids.Add((await client.PingAsync()).RequestId);

        Assert.True(ids.Distinct().Count() == ids.Count,
            "SERVER_SPEC.md § 2: X-Request-Id is \"opaque, unique per request\". Five pings produced " +
            $"[{string.Join(", ", ids)}].");
    }

    // ---- § 2, JSON media type and caching -------------------------------------------------------

    [Fact]
    public async Task JsonResponsesAreUtf8AndNoStore()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var probes = new List<Rm2Response>
        {
            await client.PingAsync(),
            await client.RootsAsync(),
            await client.BrowseAsync(Path.GetTempPath()),
            await client.OpenSessionAsync(folder.Path),
            await client.GetSessionAsync(),
            await client.GetPairAsync(),
            await client.SaveAsync(),
            await client.MetaAsync(MediaFixtures.PlainJpeg)
        };

        foreach (var probe in probes)
        {
            var type = probe.ContentType;
            Assert.True(type is not null && type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase),
                $"SERVER_SPEC.md § 2: JSON response bodies are `application/json; charset=utf-8`. " +
                $"{probe.Method} {probe.Url} answered with '{type ?? "(none)"}'.\n{probe.Describe(200)}");

            Assert.True(type!.Replace(" ", "").Contains("charset=utf-8", StringComparison.OrdinalIgnoreCase),
                $"SERVER_SPEC.md § 2 spells the JSON content type `application/json; charset=utf-8`. " +
                $"{probe.Method} {probe.Url} answered '{type}'.");

            probe.Header("Cache-Control", "no-store",
                $"SERVER_SPEC.md § 2: \"Every JSON response MUST carry Cache-Control: no-store\" " +
                $"({probe.Method} {probe.Url})");
        }
    }

    [Fact]
    public async Task ErrorEnvelopesAreAlsoNoStore()
    {
        var client = await ClientAsync();

        var probes = new[]
        {
            await client.GetSessionAsync(),
            await client.GetAsync("/no-such-route"),
            await Anonymous.GetSessionAsync()
        };

        foreach (var probe in probes)
            probe.Header("Cache-Control", "no-store",
                "SERVER_SPEC.md § 2: every JSON response is no-store, and § 12.5.5 makes media bytes the " +
                $"only cacheable responses ({probe.Method} {probe.Url} → {probe.StatusCode})");
    }

    // ---- § 2, request content type --------------------------------------------------------------

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/xml")]
    public async Task NonJsonContentTypeIsRejected(string contentType)
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/session", customise: request =>
        {
            request.Content = new StringContent($"{{\"folder\":{JsonSerializer.Serialize(folder.Path)}}}",
                                                Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        });

        response.Error("unsupported_content_type",
            $"SERVER_SPEC.md § 2: a JSON request body with Content-Type '{contentType}' is " +
            "415 unsupported_content_type");
    }

    [Fact]
    public async Task MissingContentTypeOnABodiedPostIsRejected()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/session", customise: request =>
        {
            request.Content = new ByteArrayContent(
                Encoding.UTF8.GetBytes($"{{\"folder\":{JsonSerializer.Serialize(folder.Path)}}}"));
            request.Content.Headers.ContentType = null;
        });

        Assert.True(response.StatusCode is 415 or 400,
            "SERVER_SPEC.md § 2 admits `application/json` and nothing else, so a body with no " +
            $"Content-Type at all cannot be accepted. Got {response.StatusCode}.\n{response.Describe()}");
    }

    // ---- § 2 / § 15, body size ------------------------------------------------------------------

    [Fact]
    public async Task BodyOverSixtyFiveKilobytesIsRejected()
    {
        var client = await ClientAsync();

        // Valid JSON, oversized only because of one enormous ignored field: this must fail on the
        // limit, not on parsing.
        var padding = new string('x', 70_000);
        var body = $"{{\"folder\":\"/tmp\",\"padding\":\"{padding}\"}}";
        Assert.True(Encoding.UTF8.GetByteCount(body) > Contract.MaxJsonBodyBytes);

        var response = await client.SendAsync(HttpMethod.Post, "/session", body);
        var error = response.Error("payload_too_large",
            "SERVER_SPEC.md § 2 and § 15: a JSON body over 65536 bytes is 413 payload_too_large");

        var details = response.Details(error, "SERVER_SPEC.md § 5.2: payload_too_large carries details.maxBytes");
        Assert.True(details.GetProperty("maxBytes").GetInt32() == Contract.MaxJsonBodyBytes,
            $"SERVER_SPEC.md § 5.2 fixes details.maxBytes at {Contract.MaxJsonBodyBytes}; got " +
            $"{details.GetProperty("maxBytes")}.");
    }

    [Fact]
    public async Task BodyJustUnderTheLimitIsAccepted()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var head = $"{{\"folder\":{JsonSerializer.Serialize(folder.Path)},\"padding\":\"";
        var tail = "\"}";
        var padding = new string('x', Contract.MaxJsonBodyBytes - head.Length - tail.Length - 8);
        var body = head + padding + tail;
        Assert.True(Encoding.UTF8.GetByteCount(body) <= Contract.MaxJsonBodyBytes);

        var response = await client.SendAsync(HttpMethod.Post, "/session", body);
        Assert.True(response.StatusCode == 201,
            "SERVER_SPEC.md § 2 sets the limit at 65536 bytes, and § 2 also says unknown JSON fields are " +
            $"ignored — a body of {Encoding.UTF8.GetByteCount(body)} bytes must open the session. " +
            $"Got {response.StatusCode}.\n{response.Describe()}");
    }

    // ---- § 2, unknown and null fields -----------------------------------------------------------

    [Fact]
    public async Task UnknownJsonFieldsAreIgnored()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/session",
            new { folder = folder.Path, somethingTheServerNeverHeardOf = 42, nested = new { a = "b" } });

        Assert.True(response.StatusCode == 201,
            "SERVER_SPEC.md § 2: \"Unknown JSON fields: ignored; the server MUST NOT reject a request for " +
            $"them\". Got {response.StatusCode}.\n{response.Describe()}");
    }

    [Fact]
    public async Task NullOptionalFieldIsTreatedAsAbsent()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();

        var opened = (await client.OpenSessionAsync(folder.Path))
            .RequireSnapshot(201, "SERVER_SPEC.md § 10.1: a rankable folder opens with 201");
        var token = opened.RequireToken("§ 8: a ranking session has a pairToken");

        var response = await client.SendAsync(HttpMethod.Post, "/session/vote",
            $"{{\"pairToken\":{JsonSerializer.Serialize(token)},\"winner\":\"left\",\"clientRequestId\":null}}");

        var snapshot = response.RequireSnapshot(200,
            "SERVER_SPEC.md § 2: \"Missing/null optional fields: treated as absent\" — an explicit " +
            "clientRequestId of null is the same as sending none");

        var last = snapshot.RequireLastAction("§ 9.4");
        Assert.True(last.ClientRequestId is null,
            "SERVER_SPEC.md § 9.4: clientRequestId is \"echoed verbatim from the request body, or null if " +
            $"the client sent none\". A literal null was sent and lastAction.clientRequestId is '{last.ClientRequestId}'.");
    }

    // ---- § 2, malformed bodies ------------------------------------------------------------------

    [Fact]
    public async Task MalformedJsonIsInvalidRequest()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{ this is not json ");

        response.Error("invalid_request",
            "SERVER_SPEC.md § 5.2: \"body is not valid JSON\" → 400 invalid_request");
    }

    [Fact]
    public async Task EmptyBodyOnAnEndpointThatRequiresOneIsRejected()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "");

        var code = response.ErrorCodeOrThrow("SERVER_SPEC.md § 4: every non-2xx carries the envelope");
        Assert.True(code is "invalid_request" or "missing_field",
            "SERVER_SPEC.md § 5.2: an empty body for POST /session is either invalid_request (not valid " +
            $"JSON) or missing_field (folder absent). Got '{code}'.\n{response.Describe()}");
    }

    [Fact]
    public async Task JsonArrayWhereAnObjectIsRequiredIsInvalidRequest()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "[1,2,3]");

        var code = response.ErrorCodeOrThrow("SERVER_SPEC.md § 4");
        Assert.True(code is "invalid_request" or "missing_field",
            $"SERVER_SPEC.md § 5.2: a JSON array where OpenSessionRequest is required is a 400. Got '{code}' " +
            $"at HTTP {response.StatusCode}.\n{response.Describe()}");
    }

    [Fact]
    public async Task WrongJsonTypeForAFieldIsInvalidRequest()
    {
        var client = await ClientAsync();
        var response = await client.SendAsync(HttpMethod.Post, "/session", "{\"folder\": 12345}");

        var error = response.Error("invalid_request",
            "SERVER_SPEC.md § 5.2: \"a field has the wrong JSON type\" → 400 invalid_request");
        var details = response.Details(error, "SERVER_SPEC.md § 5.2: invalid_request carries details.field");
        Assert.True(details.GetProperty("field").GetString() == "folder",
            $"details.field should name the offending field 'folder'; got '{details.GetProperty("field")}'.");
    }

    // ---- § 2, HEAD on the media endpoints -------------------------------------------------------

    [Fact]
    public async Task HeadMatchesGetOnEveryMediaEndpoint()
    {
        using var folder = LibraryFolder.Mixed();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        var paths = new[]
        {
            $"/media/{MediaFixtures.PlainJpeg}/meta",
            $"/media/{MediaFixtures.PlainJpeg}/still?w=720",
            $"/media/{MediaFixtures.PlainJpeg}/thumb",
            $"/media/{MediaFixtures.Video}/video"
        };

        foreach (var path in paths)
        {
            var get = await client.GetAsync(path);
            var head = await client.HeadAsync(path);

            Assert.True(head.StatusCode == get.StatusCode,
                "SERVER_SPEC.md § 2: \"HEAD MUST be supported on all four /media endpoints and MUST return " +
                $"the identical status line and headers as GET with no body\". GET {path} → {get.StatusCode}, " +
                $"HEAD → {head.StatusCode}.\n{head.Describe()}");

            Assert.True(head.Body.Length == 0,
                $"SERVER_SPEC.md § 2: HEAD returns no body. HEAD {path} returned {head.Body.Length} bytes.");

            foreach (var header in new[] { "Content-Type", "ETag", "Cache-Control", "Accept-Ranges", "Vary" })
            {
                var expected = get.HeaderOrNull(header);
                var actual = head.HeaderOrNull(header);
                Assert.True(expected == actual,
                    $"SERVER_SPEC.md § 2: HEAD must return the identical headers to GET. For {path}, " +
                    $"{header} is '{expected ?? "(absent)"}' on GET and '{actual ?? "(absent)"}' on HEAD.");
            }

            var length = head.Raw.Content.Headers.ContentLength;
            Assert.True(length is null || length == get.Body.Length,
                $"SERVER_SPEC.md § 2: HEAD must report what GET would send. For {path}, GET sent " +
                $"{get.Body.Length} bytes and HEAD advertises Content-Length {length}.");
        }
    }

    [Fact]
    public async Task HeadOnAMissingIdIsTheSameFailureAsGet()
    {
        using var folder = LibraryFolder.SixStills();
        var client = await ClientAsync();
        await client.OpenSessionAsync(folder.Path);

        var get = await client.GetAsync("/media/nothing-here.jpg/meta");
        var head = await client.HeadAsync("/media/nothing-here.jpg/meta");

        Assert.True(head.StatusCode == get.StatusCode,
            "SERVER_SPEC.md § 2: HEAD returns the identical status line to GET, failures included. " +
            $"GET → {get.StatusCode}, HEAD → {head.StatusCode}.\n{head.Describe()}");
    }

    // ---- § 1.1, rename is absent ----------------------------------------------------------------

    [Theory]
    [InlineData("/session/rename")]
    [InlineData("/rename")]
    [InlineData("/libraries/rename")]
    [InlineData("/session/renameByRank")]
    public async Task NoRouteContainsRename(string path)
    {
        var client = await ClientAsync();

        var get = await client.GetAsync(path);
        var post = await client.SendAsync(HttpMethod.Post, path, new { });

        foreach (var response in new[] { get, post })
            response.Error("not_found",
                "SERVER_SPEC.md § 1.1: \"A request to any path containing `rename` MUST fall through to the " +
                $"normal 404 not_found\" ({response.Method} {path})");
    }

    [Fact]
    public async Task UnknownRoutesCarryTheErrorEnvelope()
    {
        var client = await ClientAsync();
        var response = await client.GetAsync("/definitely/not/a/route");

        response.Error("not_found",
            "SERVER_SPEC.md § 4: \"There is no second error shape anywhere in the API, including for " +
            "auth failures, routing 404s and unhandled exceptions\"");
    }
}
