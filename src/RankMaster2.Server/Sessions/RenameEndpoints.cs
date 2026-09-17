namespace RankMaster2.Server.Sessions;

/// <summary>
/// Route registration for the rename group (SERVER_SPEC.md § 10.16): <c>POST /session/rename</c>,
/// <c>GET /session/rename</c>, <c>POST /session/rename/cancel</c>. One call from <c>Rm2Host</c>,
/// mirroring <see cref="SessionEndpoints"/>.
/// </summary>
public static class RenameEndpoints
{
    public static IEndpointRouteBuilder MapRenameEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetService<SessionRegistry>() ?? SessionRegistry.Shared;
        var group = endpoints.MapGroup($"{SessionRegistry.ApiBase}/session/rename");

        // Start. 202 is an acknowledgement, not a completion (§ 13.1's named exception) — the
        // journal is already fsynced by the time this returns, but the moves and the database
        // commit have not necessarily happened yet.
        //
        // The body is optional and carries at most a clientRequestId; a pairToken sent in it is
        // ignored, because a rename is not pair-scoped. "Ignored" is not the same as "never read":
        // § 10.16 and openapi.yaml both promise that a body which is present and malformed is a 400
        // like every other POST here, and a route that accepts `{ not json` with a 202 teaches a
        // client that this one endpoint has different rules (C9).
        group.MapPost("", async (HttpContext http, CancellationToken cancellation) =>
        {
            var (body, bodyError) = await SessionBody.ReadAsync(http, required: false);
            var idError = SessionBody.OptionalClientRequestId(body, out var clientRequestId);
            bodyError ??= idError;
            if (bodyError is not null)
                return SessionResults.Error(http, bodyError.Code, bodyError.Message, bodyError.Details);

            var outcome = await registry.StartRenameAsync(clientRequestId, cancellation);
            if (!outcome.IsError)
                http.Response.Headers.Location = $"{SessionRegistry.ApiBase}/session/rename";
            return Respond(http, outcome);
        });

        // Observe. Lock-free (§ 3.4): the poll for the bar must never queue behind the run itself.
        group.MapGet("", async (HttpContext http, CancellationToken cancellation) =>
            Respond(http, await registry.GetRenameAsync(cancellation)));

        // Cancel. Stop and reunite in place, never a rollback (§ 3.5). Idempotent.
        group.MapPost("/cancel", async (HttpContext http, CancellationToken cancellation) =>
            Respond(http, await registry.CancelRenameAsync(cancellation)));

        return endpoints;
    }

    private static IResult Respond(HttpContext http, RenameOutcome outcome)
    {
        if (outcome.IsError)
        {
            return SessionResults.Error(
                http, outcome.ErrorCode!, outcome.ErrorMessage!, outcome.ErrorDetails, outcome.ErrorSession);
        }

        return SessionResults.Operation(http, outcome.Operation!, outcome.Status);
    }
}
