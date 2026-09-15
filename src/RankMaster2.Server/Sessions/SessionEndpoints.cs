using System.Text.Json;
using RankMaster2.Server.Contracts;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// Route registration for the whole <c>/session*</c> group (SERVER_SPEC.md § 10.1 – § 10.10).
/// One call from <c>Program.cs</c>, everything else in this folder.
/// </summary>
public static class SessionEndpoints
{
    /// <summary>
    /// Maps <c>POST/GET/DELETE /api/v1/session</c>, <c>GET /api/v1/session/pair</c> and the six
    /// action routes. The registry comes from DI when one is registered — which is how a test
    /// injects a fake catalog — and otherwise from <see cref="SessionRegistry.Shared"/>, because
    /// exactly one session exists server-wide.
    /// </summary>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetService<SessionRegistry>() ?? SessionRegistry.Shared;
        var group = endpoints.MapGroup($"{SessionRegistry.ApiBase}/session");

        // § 10.1. 201 with Location when a session opened, 200 when the same folder was already
        // open — that case is a pure read and must not call Start() again.
        group.MapPost("", async (HttpContext http, CancellationToken cancellation) =>
        {
            var (body, bodyError) = await SessionBody.ReadAsync(http, required: true);
            if (bodyError is not null)
                return Fail(http, bodyError);

            var outcome = await registry.OpenAsync(body, cancellation);
            if (outcome.Status == StatusCodes.Status201Created)
                http.Response.Headers.Location = $"{SessionRegistry.ApiBase}/session";

            return Respond(http, outcome);
        });

        // § 10.2. Pure read; never an impression, never an advance.
        group.MapGet("", async (HttpContext http, CancellationToken cancellation) =>
            Respond(http, await registry.ReadAsync(cancellation)));

        // § 10.3. A convenience alias returning the identical snapshot, not a narrower resource.
        group.MapGet("/pair", async (HttpContext http, CancellationToken cancellation) =>
            Respond(http, await registry.ReadAsync(cancellation)));

        // § 10.4. Releases the lock and writes nothing. A repeated DELETE is a 404, not a failure.
        group.MapDelete("", async (HttpContext http, CancellationToken cancellation) =>
            Respond(http, await registry.CloseAsync(cancellation)));

        // § 10.5. The Ctrl+S equivalent. Idempotent; allowed while exhausted.
        group.MapPost("/save", async (HttpContext http, CancellationToken cancellation) =>
            Respond(http, await registry.SaveAsync(cancellation)));

        // § 10.6.
        group.MapPost("/vote", async (HttpContext http, CancellationToken cancellation) =>
        {
            // The parse failure travels with the body instead of answering here: § 8.4 puts the
            // no-session check ahead of it, and only the registry, holding the lock, knows.
            var (body, bodyError) = await SessionBody.ReadAsync(http, required: true);
            return Respond(http, await registry.VoteAsync(body, bodyError, cancellation));
        });

        // § 10.7.
        group.MapPost("/skip", async (HttpContext http, CancellationToken cancellation) =>
        {
            var (body, bodyError) = await SessionBody.ReadAsync(http, required: true);
            return Respond(http, await registry.SkipAsync(body, bodyError, cancellation));
        });

        // § 10.8.
        group.MapPost("/discard", async (HttpContext http, CancellationToken cancellation) =>
        {
            var (body, bodyError) = await SessionBody.ReadAsync(http, required: true);
            return Respond(http, await registry.MoveAsync(body, bodyError, special: false, cancellation));
        });

        // § 10.9. Identical to discard but for the destination and lastAction.type.
        group.MapPost("/special", async (HttpContext http, CancellationToken cancellation) =>
        {
            var (body, bodyError) = await SessionBody.ReadAsync(http, required: true);
            return Respond(http, await registry.MoveAsync(body, bodyError, special: true, cancellation));
        });

        // § 10.10. No pairToken, deliberately: the move being reversed belongs to an earlier pair
        // generation, so any token the client holds for it is stale by construction. A pairToken
        // sent in the body is ignored. The body itself is optional.
        group.MapPost("/undo", async (HttpContext http, CancellationToken cancellation) =>
        {
            var (body, bodyError) = await SessionBody.ReadAsync(http, required: false);
            return Respond(http, await registry.UndoAsync(body, bodyError, cancellation));
        });

        return endpoints;
    }

    private static IResult Respond(HttpContext http, SessionOutcome outcome)
    {
        if (outcome.IsError)
        {
            return SessionResults.Error(
                http,
                outcome.ErrorCode!,
                outcome.ErrorMessage!,
                outcome.ErrorDetails,
                outcome.ErrorSession);
        }

        if (outcome.Status == StatusCodes.Status204NoContent)
        {
            SessionResults.RequestIdOf(http);
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.NoContent();
        }

        return SessionResults.Snapshot(http, outcome.Snapshot!, outcome.Status);
    }

    private static IResult Fail(HttpContext http, BodyError error) =>
        SessionResults.Error(http, error.Code, error.Message, error.Details);
}
