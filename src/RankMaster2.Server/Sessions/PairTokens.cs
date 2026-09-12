using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// SERVER_SPEC.md § 8.2. The token is an HMAC over the session secret and the pair *sequence*, not
/// over the ids: <c>Advance()</c> clears <c>Current</c> before <c>Pick()</c> so a two-file library
/// re-pairs the same two ids forever (§ 7.4.1), and a token keyed by ids would never change there.
/// Deriving it rather than drawing a random one also means a rolled-back vote yields exactly the
/// token the client already holds, with no undo bookkeeping (§ 8.2).
/// </summary>
internal static class PairTokens
{
    private const byte Separator = 0x1F;
    public const int TokenLength = 22;

    /// <summary>
    /// <c>base64url(HMAC-SHA256(secret, sessionId | 0x1F | pairSeq | 0x1F | left | 0x1F | right))</c>
    /// truncated to 22 characters — 132 bits, no padding. All strings are UTF-8, unnormalised,
    /// exactly as <c>MediaId.Filename</c> holds them.
    /// </summary>
    public static string Generate(
        byte[] sessionSecret,
        string sessionId,
        ulong pairSeq,
        string leftFilename,
        string rightFilename)
    {
        var message = new MemoryStream();
        Append(message, sessionId);
        message.WriteByte(Separator);
        Append(message, pairSeq.ToString(CultureInfo.InvariantCulture));
        message.WriteByte(Separator);
        Append(message, leftFilename);
        message.WriteByte(Separator);
        Append(message, rightFilename);

        var mac = HMACSHA256.HashData(sessionSecret, message.ToArray());
        return Base64Url(mac)[..TokenLength];
    }

    /// <summary>
    /// Equality only, in constant time. The token is an optimistic-concurrency tag, not a
    /// credential (§ 8.1), but comparing it in constant time costs nothing and keeps the habit.
    /// </summary>
    public static bool Matches(string? supplied, string? current)
    {
        if (supplied is null || current is null)
            return false;

        var a = Encoding.UTF8.GetBytes(supplied);
        var b = Encoding.UTF8.GetBytes(current);
        if (a.Length != b.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>32 cryptographically random bytes, created when the session opens, never leaving the process.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(32);

    /// <summary>An opaque 22-character base64url session id (§ 9.1).</summary>
    public static string NewSessionId() => Base64Url(RandomNumberGenerator.GetBytes(17))[..TokenLength];

    private static void Append(Stream to, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        to.Write(bytes, 0, bytes.Length);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
