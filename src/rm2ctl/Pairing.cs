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

    /// <summary>Names a server may publish the open window under.</summary>
    public static readonly string[] OfferFileNames = { "pairing.json", "pair.offer", "pairing.offer.json" };

    public sealed record Offer(string Code, string CodeDisplay, string? Payload, string? Fingerprint, string? ExpiresAt);

    /// <summary>Where the server keeps its certificate, device store and pairing offer.</summary>
    public static string DefaultDataDirectory()
    {
        var explicitPath = Environment.GetEnvironmentVariable("RM2_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath.Trim());

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "RankMaster2", "server");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           ".local", "share", "rankmaster2", "server")
            : Path.Combine(xdg, "rankmaster2", "server");
    }

    /// <summary>
    /// Read the published offer, asking for a window first if none is open. Returns null if the
    /// server publishes nothing within <paramref name="timeout"/>.
    /// </summary>
    public static async Task<Offer?> ReadOfferAsync(string dataDirectory, TimeSpan timeout, bool request, Journal journal)
    {
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
            foreach (var name in OfferFileNames)
            {
                var offer = TryRead(Path.Combine(dataDirectory, name));
                if (offer is not null) return offer;
            }

            await Task.Delay(200);
        }

        return null;
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

            return new Offer(
                code,
                Read(root, "codeDisplay") ?? Display(code),
                payload,
                Read(root, "certificateFingerprint"),
                Read(root, "expiresAt"));
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
