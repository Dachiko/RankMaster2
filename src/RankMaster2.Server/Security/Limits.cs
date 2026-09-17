namespace RankMaster2.Server.Security;

/// <summary>
/// SERVER_SPEC.md § 15: the request-body limit, in one place. Before this, <c>65536</c> was written
/// three times — <c>SecurityMiddleware</c>'s own constant, <c>Sessions/SessionResults.MaxBytes</c>,
/// and a literal in <c>/ping</c>'s <c>limits.maxJsonBodyBytes</c> — and the body-size and
/// content-type checks ran twice per request as a result (C14). This is the one value; everything
/// that needs it points here, including <c>Sessions/SessionResults.MaxBytes</c>, which is
/// S-SESSIONS's file to update.
/// </summary>
public static class Limits
{
    public const int MaxJsonBodyBytes = 65536;
}
