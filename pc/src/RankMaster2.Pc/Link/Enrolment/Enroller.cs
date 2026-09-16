using RankMaster2.Pc.Link.Transport;
using RankMaster2.Pc.Link.Wire;

namespace RankMaster2.Pc.Link.Enrolment;

internal enum EnrolFailureReason { NoServerFound, InvalidPairingCode, PairingNotOpen, RateLimited, Unreachable }

internal abstract record EnrolOutcome
{
    public sealed record Enrolled(Credential Credential) : EnrolOutcome;
    public sealed record Failed(EnrolFailureReason Reason, string? Detail) : EnrolOutcome;
}

/// <summary>
/// The pairing sequence of § 5.2.3. The order is normative (SERVER_SPEC.md § 10.1.1): pin the
/// fingerprint before sending the code, because the code is a bearer secret and handing it to an
/// unverified TLS peer hands it to whoever answered.
/// </summary>
internal static class Enroller
{
    /// <param name="httpFor">Builds an <see cref="Rm2Http"/> pinned to an offer's base URL and
    /// fingerprint (arguments: baseUrl, fingerprint). Supplied by the caller so the test harness's
    /// <c>wrap</c> hook (§ 4.3) is applied to every handshake this makes, including the one against a
    /// re-minted certificate.</param>
    public static async Task<EnrolOutcome> Enrol(
        Func<string, string, Rm2Http> httpFor,
        string serverDataDirectory,
        TimeSpan offerTimeout,
        TimeSpan connectTimeout,
        string deviceName,
        Credential? previous,
        Offer? offerInHand,
        CancellationToken ct)
    {
        var offer = offerInHand ?? OfferChannel.Request(serverDataDirectory, offerTimeout);
        if (offer is null)
            return new EnrolOutcome.Failed(EnrolFailureReason.NoServerFound, serverDataDirectory);

        var outcome = await TryPair(httpFor, offer, deviceName, connectTimeout, ct).ConfigureAwait(false);

        // § 5.2.3: an expired-window race — the offer read may have been the tail of an earlier
        // window — is one 401 invalid_pairing_code. Ask for one fresh window and try once more.
        if (outcome is EnrolOutcome.Failed { Reason: EnrolFailureReason.InvalidPairingCode })
        {
            var fresh = OfferChannel.Request(serverDataDirectory, offerTimeout);
            if (fresh is not null)
                outcome = await TryPair(httpFor, fresh, deviceName, connectTimeout, ct).ConfigureAwait(false);
        }

        if (outcome is EnrolOutcome.Enrolled enrolled && previous is { DeviceId.Length: > 0 })
            await RevokeBestEffort(httpFor, offer, enrolled.Credential.Token, previous.DeviceId, connectTimeout, ct)
                .ConfigureAwait(false);

        return outcome;
    }

    private static async Task<EnrolOutcome> TryPair(
        Func<string, string, Rm2Http> httpFor, Offer offer, string deviceName, TimeSpan timeout, CancellationToken ct)
    {
        using var http = httpFor(offer.BaseUrl, offer.CertificateFingerprint);

        // POST /pair is NEVER retried (§ 13.3): the code is single-use, and a retry of a landed
        // pairing loses the token.
        var reply = await http.Send(FrozenRequest.Pair(offer.Code, deviceName), timeout, ct).ConfigureAwait(false);

        switch (reply)
        {
            case Reply.Ok(var status, var body, _) when status is 200 or 201:
                PairedDevice paired;
                try
                {
                    paired = System.Text.Json.JsonSerializer.Deserialize(body, WireJsonContext.Default.PairedDevice)
                        ?? throw new System.Text.Json.JsonException("null");
                }
                catch (System.Text.Json.JsonException)
                {
                    return new EnrolOutcome.Failed(EnrolFailureReason.Unreachable, "The server's /pair response did not parse.");
                }
                var credential = new Credential(offer.BaseUrl, offer.CertificateFingerprint, paired.Token, paired.DeviceId, paired.IssuedAt);
                return new EnrolOutcome.Enrolled(credential);

            case Reply.Refused(_, Codes.InvalidPairingCode, var message, _, _, _):
                return new EnrolOutcome.Failed(EnrolFailureReason.InvalidPairingCode, message);

            case Reply.Refused(_, Codes.PairingNotOpen, var message, _, _, _):
                return new EnrolOutcome.Failed(EnrolFailureReason.PairingNotOpen, message);

            case Reply.Refused(_, Codes.TooManyRequests, var message, _, _, _):
                return new EnrolOutcome.Failed(EnrolFailureReason.RateLimited, message);

            case Reply.Refused(var status, var code, var message, _, _, _):
                return new EnrolOutcome.Failed(EnrolFailureReason.Unreachable, $"{status} {code}: {message}");

            case Reply.Unreachable(_, var detail):
                return new EnrolOutcome.Failed(EnrolFailureReason.Unreachable, detail);

            default:
                return new EnrolOutcome.Failed(EnrolFailureReason.Unreachable, "unexpected reply");
        }
    }

    /// <summary>§ 5.2.3 step 4: so re-enrolment does not grow the server's device list. Best effort —
    /// its result is never surfaced to the owner.</summary>
    private static async Task RevokeBestEffort(
        Func<string, string, Rm2Http> httpFor, Offer offer, string newToken, string oldDeviceId, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var http = httpFor(offer.BaseUrl, offer.CertificateFingerprint);
            http.BearerToken = newToken;
            await http.Send(FrozenRequest.Delete("/pair/" + Uri.EscapeDataString(oldDeviceId)), timeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // best effort
            _ = e;
        }
    }
}
