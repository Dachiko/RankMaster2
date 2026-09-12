using System.Net;
using System.Text;
using System.Text.Json;
using Xunit.Sdk;

namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// One HTTP exchange, kept whole so a failure can explain itself.
///
/// The suite is written while three other people are still building the server, so most failures
/// will be "not implemented yet" rather than "implemented wrongly". A bare `Assert.Equal(201, 404)`
/// cannot tell those apart; this can, and says which clause of the contract it was checking.
/// </summary>
public sealed class Rm2Response
{
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required HttpStatusCode Status { get; init; }
    public required HttpResponseMessage Raw { get; init; }
    public required byte[] Body { get; init; }

    public int StatusCode => (int)Status;
    public string? ContentType => Raw.Content.Headers.ContentType?.ToString();
    public string? RequestId => HeaderOrNull("X-Request-Id");

    private JsonElement? _json;
    private bool _jsonParsed;

    /// <summary>The body as JSON, or null when it is not JSON or will not parse.</summary>
    public JsonElement? Json
    {
        get
        {
            if (_jsonParsed) return _json;
            _jsonParsed = true;
            if (Body.Length == 0) return _json = null;
            try
            {
                _json = JsonDocument.Parse(Body).RootElement.Clone();
            }
            catch (JsonException)
            {
                _json = null;
            }
            return _json;
        }
    }

    /// <summary>The JSON body, or a failure that shows what came back instead.</summary>
    public JsonElement JsonBody =>
        Json ?? throw Failure("Expected a JSON body and did not get one.");

    public string Text => Encoding.UTF8.GetString(Body);

    public string? HeaderOrNull(string name)
    {
        if (Raw.Headers.TryGetValues(name, out var values)) return string.Join(", ", values);
        if (Raw.Content.Headers.TryGetValues(name, out var contentValues)) return string.Join(", ", contentValues);
        return null;
    }

    public string Header(string name, string clause) =>
        HeaderOrNull(name) ?? throw Failure($"{clause}: the response carries no {name} header.");

    // ---- assertions -------------------------------------------------------------------------

    public Rm2Response ShouldHaveStatus(int expected, string clause)
    {
        if (StatusCode == expected) return this;
        throw Failure($"{clause}: expected HTTP {expected}, got {StatusCode}.{NotImplementedHint(expected)}");
    }

    /// <summary>A successful snapshot response: the status, then the exact object shape of § 9.1.</summary>
    public Snapshot ShouldBeSnapshot(int expected, string clause)
    {
        ShouldHaveStatus(expected, clause);

        var json = Json ?? throw Failure($"{clause}: expected a SessionSnapshot body, but the body is not JSON.");
        ContractShape.RequireSnapshot(json, $"{clause} (SERVER_SPEC.md § 9.1)", this);
        return new Snapshot(json, this);
    }

    /// <summary>The single error envelope of § 4, carrying the code § 5 binds to this status.</summary>
    public ApiFailure ShouldBeError(string expectedCode, string clause)
    {
        var expectedStatus = ErrorStatuses.Of(expectedCode);
        var json = Json ?? throw Failure(
            $"{clause}: expected the error envelope with code '{expectedCode}', but the body is not JSON.");

        if (!json.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            throw Failure($"{clause}: expected an object at 'error' (SERVER_SPEC.md § 4 — there is no second error shape).");

        ContractShape.RequireErrorBody(error, $"{clause} (SERVER_SPEC.md § 4)", this);

        var actualCode = error.GetProperty("code").GetString();
        if (actualCode != expectedCode)
            throw Failure($"{clause}: expected error.code '{expectedCode}', got '{actualCode}'.");

        if (StatusCode != expectedStatus)
            throw Failure($"{clause}: '{expectedCode}' is bound to HTTP {expectedStatus} by SERVER_SPEC.md § 5, " +
                          $"but this response was {StatusCode}. A code's status is part of the contract.");

        var requestId = error.GetProperty("requestId").GetString();
        if (requestId != RequestId)
            throw Failure($"{clause}: error.requestId is '{requestId}' but the X-Request-Id header is " +
                          $"'{RequestId}'. SERVER_SPEC.md § 4 requires them to be equal.");

        return new ApiFailure(error, this);
    }

    /// <summary>Any of several codes — for the few places the contract genuinely allows a choice.</summary>
    public ApiFailure ShouldBeErrorOneOf(string clause, params string[] codes)
    {
        var json = Json;
        var actual = json?.TryGetProperty("error", out var error) == true && error.ValueKind == JsonValueKind.Object
            ? error.TryGetProperty("code", out var code) ? code.GetString() : null
            : null;

        if (actual is not null && codes.Contains(actual)) return ShouldBeError(actual, clause);
        throw Failure($"{clause}: expected one of [{string.Join(", ", codes)}], got " +
                      $"{(actual is null ? "no error envelope" : $"'{actual}'")}.");
    }

    private string NotImplementedHint(int expected)
    {
        if (StatusCode != 404 || expected == 404) return "";
        var hasEnvelope = Json?.TryGetProperty("error", out _) == true;
        return hasEnvelope
            ? ""
            : "\n  The 404 carries no error envelope, so this route is most likely not implemented yet " +
              "rather than answering 'not found'.";
    }

    public XunitException Failure(string message) => new(message + "\n" + Describe());

    public string Describe(int bodyLimit = 1200)
    {
        var text = new StringBuilder();
        text.Append("  request : ").Append(Method).Append(' ').Append(Url).AppendLine();
        text.Append("  status  : ").Append(StatusCode).Append(' ').Append(Status).AppendLine();
        text.Append("  type    : ").Append(ContentType ?? "(none)").AppendLine();
        text.Append("  reqId   : ").Append(RequestId ?? "(absent — SERVER_SPEC.md § 2 requires X-Request-Id on every response)").AppendLine();

        var body = Body.Length == 0 ? "(empty)" : LooksTextual() ? Text : $"({Body.Length} bytes of {ContentType ?? "binary"})";
        if (body.Length > bodyLimit) body = body[..bodyLimit] + $"… (+{body.Length - bodyLimit} more chars)";
        text.Append("  body    : ").Append(body);
        return text.ToString();
    }

    private bool LooksTextual() =>
        ContentType is null ||
        ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
        ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The parsed <c>error</c> object, with accessors for the per-code `details` of § 5.</summary>
public sealed class ApiFailure(JsonElement error, Rm2Response response)
{
    public JsonElement Error { get; } = error;
    public Rm2Response Response { get; } = response;

    public string Code => Error.GetProperty("code").GetString()!;
    public string Message => Error.GetProperty("message").GetString()!;

    public JsonElement? Details =>
        Error.TryGetProperty("details", out var details) && details.ValueKind is not JsonValueKind.Null
            ? details
            : null;

    /// <summary>`error.session` — present iff a session is open when the error is produced (§ 4).</summary>
    public Snapshot? Session =>
        Error.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object
            ? new Snapshot(session, Response)
            : null;

    public JsonElement Detail(string name, string clause)
    {
        var details = Details ?? throw Response.Failure(
            $"{clause}: '{Code}' must carry error.details (SERVER_SPEC.md § 5), but details is absent or null.");

        if (!details.TryGetProperty(name, out var value))
            throw Response.Failure($"{clause}: error.details for '{Code}' must contain '{name}' (SERVER_SPEC.md § 5).");

        return value;
    }

    public Snapshot RequireSession(string clause) =>
        Session ?? throw Response.Failure(
            $"{clause}: this error must embed the full SessionSnapshot at error.session " +
            "(SERVER_SPEC.md § 4 — required for every 409 on a /session* endpoint, and § 8.5 — it is what " +
            "resynchronises the client in one round trip).");
}
