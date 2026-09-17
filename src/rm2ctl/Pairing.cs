using System.Text.Json;
using QRCoder;

namespace RankMaster2.Cli;

/// <summary>
/// Pairing, from the owner's side of the machine (SERVER_SPEC.md § 10.11).
///
/// The pairing window is opened "out of band", and deliberately not over HTTP — an HTTP route for it
/// would let anyone on the LAN start a window and then spend the day guessing at the six digits. The
/// channel is the server's own data directory: a request file asks for a window, and the server
/// publishes the open window's code and QR payload back into the same directory, readable only by
/// the user the server runs as. So this runs on the server's host, which is the point.
/// </summary>
public static class Pairing
{
    public const string RequestFileName = "pair.request";

    /// <summary>
    /// The name the server publishes the open window under. Normative, and exactly one name:
    /// SERVER_SPEC.md § 10.1.1 fixes it because three clients had each guessed differently, and a
    /// client that also accepts a name the server never writes turns "the server published nothing"
    /// into "something published something" (C24).
    /// </summary>
    public const string OfferFileName = "pairing.json";

    public sealed record Offer(string Code, string CodeDisplay, string? Payload, string? Fingerprint, string? ExpiresAt, string? Host, int? Port);

    /// <summary>
    /// Where the server keeps its certificate, device store and pairing offer. One spelling,
    /// everywhere: <c>RankMaster2/Server</c>, capital R, capital M, capital S (SERVER_SPEC.md § 2.4).
    /// A lowercase copy is not a cosmetic difference — on Linux it is a second directory, so the
    /// tool looks for the offer in a folder the server never writes to (C12).
    /// </summary>
    public static string DefaultDataDirectory()
    {
        var explicitPath = Environment.GetEnvironmentVariable("RM2_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath.Trim());

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "RankMaster2", "Server");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           ".local", "share", "RankMaster2", "Server")
            : Path.Combine(xdg, "RankMaster2", "Server");
    }

    /// <summary>
    /// Read the published offer, asking for a window first if none is open. Returns null if the
    /// server publishes nothing within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<Offer?> ReadOfferAsync(string dataDirectory, TimeSpan timeout, bool request, Journal journal)
    {
        // The offer file already holds the PREVIOUS window. Asking for a new one and then reading
        // the file immediately hands back the old code, which the server has just retired - it comes
        // straight back as invalid_pairing_code. So remember what was there before asking, and wait
        // for the server to publish something different.
        var stale = request ? CurrentOffer(dataDirectory) : null;

        if (request)
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                await File.WriteAllTextAsync(Path.Combine(dataDirectory, RequestFileName), "");
                journal.Note($"asked for a pairing window with {RequestFileName} in {dataDirectory}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                journal.Note($"could not write {RequestFileName} in {dataDirectory}: {e.Message}");
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var offer = CurrentOffer(dataDirectory);
            if (offer is not null && (stale is null || offer.Code != stale.Code || offer.ExpiresAt != stale.ExpiresAt))
            {
                if (stale is not null)
                    journal.Note("the server published a fresh pairing window");
                return offer;
            }

            await Task.Delay(200);
        }

        // Nothing new arrived. An unchanged offer that has not expired is still worth returning:
        // the server may simply have had a window open already.
        return stale;
    }

    private static Offer? CurrentOffer(string dataDirectory) =>
        TryRead(Path.Combine(dataDirectory, OfferFileName));

    private static string? FromPayload(string? payload, string key)
    {
        if (payload is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(payload, $"[?&]{key}=([^&]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static Offer? TryRead(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;

            var code = Read(root, "code");
            var payload = Read(root, "payload");

            // Some publishers carry only the QR payload; the code is a parameter inside it.
            code ??= CodeFromPayload(payload);
            if (code is null) return null;

            var host = Read(root, "host") ?? FromPayload(payload, "host");
            var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var n)
                ? n
                : int.TryParse(FromPayload(payload, "port"), out var fromPayload) ? fromPayload : (int?)null;

            return new Offer(
                code,
                Read(root, "codeDisplay") ?? Display(code),
                payload,
                Read(root, "certificateFingerprint"),
                Read(root, "expiresAt"),
                host,
                port);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A half-written file will be complete on the next poll.
            return null;
        }

        static string? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string? CodeFromPayload(string? payload)
    {
        if (payload is null) return null;

        var marker = payload.IndexOf("code=", StringComparison.Ordinal);
        if (marker < 0) return null;

        var rest = payload[(marker + 5)..];
        var end = rest.IndexOf('&');
        return Uri.UnescapeDataString(end < 0 ? rest : rest[..end]);
    }

    private static string Display(string code) =>
        code.Length == 6 ? code[..3] + " " + code[3..] : code;

    /// <summary>
    /// Print the code big and the payload as a QR block, which is the whole user-facing point of
    /// <c>rm2ctl pair</c>: the phone scans it and gets the host, port, pinned fingerprint and code
    /// in one go, with nothing typed and nothing to mistype.
    /// </summary>
    public static void Show(Offer offer, Journal journal)
    {
        Console.WriteLine();
        Console.WriteLine("  Pairing code:  " + offer.CodeDisplay);
        if (offer.ExpiresAt is not null) Console.WriteLine("  Valid until:   " + offer.ExpiresAt);
        if (offer.Fingerprint is not null) Console.WriteLine("  Certificate:   " + offer.Fingerprint);

        if (offer.Payload is null)
        {
            Console.WriteLine();
            Console.WriteLine("  The server published no QR payload; type the code on the phone instead.");
            return;
        }

        Console.WriteLine();
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(offer.Payload, QRCodeGenerator.ECCLevel.M);
            Console.WriteLine(new AsciiQRCode(data).GetGraphicSmall());
        }
        catch (Exception e)
        {
            journal.Note($"could not render the QR code ({e.Message}); the payload is below.");
        }

        Console.WriteLine("  " + offer.Payload);
        Console.WriteLine();
        Console.WriteLine("  Scan this on the phone. The code is single-use and expires in five minutes;");
        Console.WriteLine("  a retry of a pairing that already succeeded loses the token (SERVER_SPEC.md § 13.3).");
    }
}
