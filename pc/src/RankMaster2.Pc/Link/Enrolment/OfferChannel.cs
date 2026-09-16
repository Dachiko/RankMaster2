using System.Globalization;
using System.Text.Json;

namespace RankMaster2.Pc.Link.Enrolment;

/// <summary>What the server published in <c>pairing.json</c> (SERVER_SPEC.md § 10.1.1): everything
/// needed to pin, connect and redeem the code.</summary>
internal sealed record Offer(string Host, int Port, string CertificateFingerprint, string Code, DateTimeOffset ExpiresAt)
{
    public string BaseUrl => $"https://{Host}:{Port}/api/v1";
}

/// <summary>
/// Asks the server to open a pairing window, and reads the offer it publishes.
/// <para/>
/// Lifted from <c>src/RankMaster2.Tray/PairingChannel.cs</c>'s algorithm — it already solves the
/// stale-offer trap: wait for an offer whose <c>expiresAt</c> is strictly newer than whatever was
/// already published, so a leftover <c>pairing.json</c> from an earlier window is never mistaken for
/// the answer to this request. That type is <c>internal</c> to a WinForms exe, so this is a copy, not
/// a reference, and it reads the full offer (<c>host</c>, <c>port</c>, <c>certificateFingerprint</c>,
/// <c>code</c>), not just the display fields the Tray needed.
/// <para/>
/// Split into three primitives rather than one because §5.2.2 step B and §5.2.3's own fallback need
/// different lifetimes for the sentinel file: step B writes it, may start the server itself, and
/// keeps waiting across that restart before giving up; §5.2.3's plain call wants the simpler
/// request-then-wait-then-cleanup shape. <see cref="Request"/> composes the three for that simpler case.
/// </summary>
internal static class OfferChannel
{
    private const string RequestFile = "pair.request";
    private const string OfferFile = "pairing.json";

    /// <summary>Reads whatever offer is currently published, without asking for a new one. Used to
    /// capture "previous" before writing a request, so a later wait knows what "fresh" means.</summary>
    public static Offer? ReadCurrent(string dataDirectory) => Read(Path.Combine(dataDirectory, OfferFile));

    /// <summary>Creates the sentinel file. Idempotent — the server deletes it on pickup, and writing
    /// it again is harmless if it is still there.</summary>
    public static bool RequestWindow(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllBytes(Path.Combine(dataDirectory, RequestFile), []);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Polls up to <paramref name="timeout"/> for an offer strictly newer than
    /// <paramref name="previous"/>. Does not touch the request file.</summary>
    public static Offer? WaitForOffer(string dataDirectory, Offer? previous, TimeSpan timeout)
    {
        var offerPath = Path.Combine(dataDirectory, OfferFile);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            var offer = Read(offerPath);
            if (offer is not null && (previous is null || offer.ExpiresAt > previous.ExpiresAt))
                return offer;

            if (DateTimeOffset.UtcNow >= deadline) return null;
            Thread.Sleep(150);
        }
    }

    public static void TryDeleteRequestFile(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, RequestFile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Request a fresh window and wait up to <paramref name="timeout"/> for it. On timeout, deletes
    /// the request file it wrote (§ 5.2.3): a server started later by hand should not open a pairing
    /// window nobody is still waiting for.
    /// </summary>
    public static Offer? Request(string dataDirectory, TimeSpan timeout)
    {
        var previous = ReadCurrent(dataDirectory);
        if (!RequestWindow(dataDirectory)) return null;

        var offer = WaitForOffer(dataDirectory, previous, timeout);
        if (offer is null) TryDeleteRequestFile(dataDirectory);
        return offer;
    }

    private static Offer? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;

            var host = root.TryGetProperty("host", out var h) ? h.GetString() : null;
            var port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out var portValue) ? portValue : (int?)null;
            var fingerprint = root.TryGetProperty("certificateFingerprint", out var f) ? f.GetString() : null;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var expires = root.TryGetProperty("expiresAt", out var e) ? e.GetString() : null;

            if (host is null || port is null || fingerprint is null || code is null || expires is null)
                return null;

            var expiresAt = DateTimeOffset.Parse(
                expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

            return new Offer(host, port.Value, fingerprint, code, expiresAt);
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or KeyNotFoundException)
        {
            // A half-written file, or one being replaced underneath us. The caller polls.
            return null;
        }
    }
}
