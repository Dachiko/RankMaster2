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
    /// Requests a fresh window and waits for the offer that answers it. The wait is for an offer
    /// strictly newer than whatever was already published, so a stale <c>pairing.json</c> from an
    /// earlier window is never mistaken for the answer to this request.
    /// </summary>
    public static PairingOffer Request(string dataDirectory)
    {
        var offerPath = Path.Combine(dataDirectory, OfferFile);
        var previous = Read(offerPath);

        File.WriteAllBytes(Path.Combine(dataDirectory, RequestFile), []);

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
