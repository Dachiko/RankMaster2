using System.Globalization;
using System.Text.Json;

namespace RankMaster2.Tray;

/// <summary>One published pairing window, as the server wrote it to <c>pairing.json</c>.</summary>
internal sealed record PairingOffer(string CodeDisplay, string Payload, DateTimeOffset ExpiresAt)
{
    public TimeSpan Remaining(DateTimeOffset now) => ExpiresAt - now;
}

/// <summary>
/// Asks the server to open a pairing window, and reads the offer it publishes.
/// <para/>
/// SERVER_SPEC.md § 10.1.1 fixes both file names and makes this the supported way in: create
/// <c>&lt;data&gt;/pair.request</c>, and the server — which polls once a second — opens a window and
/// writes <c>&lt;data&gt;/pairing.json</c>. Creating a file in that directory is the proof that the
/// asker controls the owner's OS account, which is the whole reason opening a window is not an HTTP
/// call.
/// </summary>
internal static class PairingChannel
{
    private const string RequestFile = "pair.request";
    private const string OfferFile = "pairing.json";

    /// <summary>The server polls at one second; this is that, with room for a slow disk.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Where the offer this window is showing lives. The security layer deletes this file the
    /// moment a window's guess budget is exhausted (SERVER_SPEC.md § 10.11) — <see
    /// cref="PairingForm"/> polls for exactly that (A30).
    /// </summary>
    public static string OfferPath(string dataDirectory) => Path.Combine(dataDirectory, OfferFile);

    /// <summary>
    /// Requests a fresh window and waits for the offer that answers it. The wait is for an offer
    /// strictly newer than whatever was already published, so a stale <c>pairing.json</c> from an
    /// earlier window is never mistaken for the answer to this request.
    /// </summary>
    public static PairingOffer Request(string dataDirectory)
    {
        var offerPath = Path.Combine(dataDirectory, OfferFile);
        var previous = Read(offerPath);
        var requestPath = Path.Combine(dataDirectory, RequestFile);

        File.WriteAllBytes(requestPath, []);

        try
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(150);

                var offer = Read(offerPath);
                if (offer is null)
                    continue;
                if (previous is not null && offer.ExpiresAt <= previous.ExpiresAt)
                    continue;

                return offer;
            }

            throw new TimeoutException(
                "The server did not open a pairing window within " +
                $"{Timeout.TotalSeconds:0} seconds. Its log is in {Path.Combine(dataDirectory, "logs")}.");
        }
        finally
        {
            // AUDIT2.md § 3.13: the server answers a live request within its own 1 s poll, so on
            // the success path above this is already gone by the time we get here — a no-op. It
            // matters on every path that is *not* success: the wait above timed out (nobody is
            // watching for the answer any more, so it must not sit here to be picked up unattended
            // by a much later, unrelated server start), or an exception unwound past the loop. The
            // server's own poll independently drops anything older than
            // PairingService.MaxPairRequestAge, so a crash between the write above and this
            // `finally` — the one case this cleanup cannot reach — is still bounded by that.
            TryDeleteRequestFile(requestPath);
        }
    }

    private static void TryDeleteRequestFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static PairingOffer? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;

            var code = root.GetProperty("codeDisplay").GetString();
            var payload = root.GetProperty("payload").GetString();
            var expires = root.GetProperty("expiresAt").GetString();

            if (code is null || payload is null || expires is null)
                return null;

            return new PairingOffer(
                code,
                payload,
                DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or KeyNotFoundException)
        {
            // A half-written file, or one being replaced underneath us. The caller polls.
            return null;
        }
    }
}
