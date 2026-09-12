using System.Text;

namespace RankMaster2.Server.Media;

/// <summary>Why an id was refused, and therefore with which error code.</summary>
public enum MediaIdFault
{
    None,

    /// <summary>Syntactically not an id → <c>400 invalid_media_id</c>.</summary>
    Invalid,

    /// <summary>Would leave the session folder → <c>403 media_outside_session</c>.</summary>
    Escapes,
}

public readonly record struct MediaIdResult(string Id, MediaIdFault Fault, string? Reason)
{
    public bool Ok => Fault == MediaIdFault.None;

    public static MediaIdResult Good(string id) => new(id, MediaIdFault.None, null);
    public static MediaIdResult Bad(string reason) => new("", MediaIdFault.Invalid, reason);
    public static MediaIdResult Escape(string reason) => new("", MediaIdFault.Escapes, reason);
}

/// <summary>
/// SERVER_SPEC.md § 11.1. An id is a filename carried as one percent-encoded path segment.
/// <para/>
/// The decode happens here, from the raw request target, rather than from the routing layer's
/// already-decoded route value. That is deliberate: § 11.1.2 says the server decodes
/// <b>exactly once</b>, and § 11.1.5 says <c>%2F</c> and <c>%5C</c> must be rejected — neither is
/// checkable once a framework has decoded the segment for you and handed back a string in which
/// an encoded separator and a literal one look identical.
/// </summary>
public static class MediaIdCodec
{
    /// <summary>§ 15: a media id is at most 255 UTF-16 code units.</summary>
    public const int MaxLength = 255;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Percent-decodes one path segment exactly once, as UTF-8, applying no normalisation and no
    /// case folding (§ 11.1.2, § 11.1.3), then validates it as a filename (§ 11.1.4).
    /// </summary>
    public static MediaIdResult Decode(string rawSegment)
    {
        if (string.IsNullOrEmpty(rawSegment))
            return MediaIdResult.Bad("empty");

        if (!TryPercentDecode(rawSegment, out var decoded, out var reason))
        {
            return reason == "separator"
                ? MediaIdResult.Escape("separator")
                : MediaIdResult.Bad(reason);
        }

        return Validate(decoded);
    }

    /// <summary>
    /// § 11.1.4 applied to an already-decoded string. Exposed separately so a caller holding only
    /// the framework's decoded route value still gets the same rules, minus the encoded-separator
    /// check that only the raw segment can answer.
    /// </summary>
    public static MediaIdResult Validate(string decoded)
    {
        if (decoded.Length == 0)
            return MediaIdResult.Bad("empty");

        if (decoded.Length > MaxLength)
            return MediaIdResult.Bad("too-long");

        foreach (var c in decoded)
        {
            if (c is '/' or '\\')
                return MediaIdResult.Escape("separator");

            // NUL and every other control character below 0x20.
            if (c < 0x20)
                return MediaIdResult.Bad("control-character");
        }

        if (decoded is "." or "..")
            return MediaIdResult.Escape("dot-segment");

        if (Path.IsPathRooted(decoded))
            return MediaIdResult.Escape("rooted");

        // Catches a Windows drive-relative spelling such as "C:name.jpg", which is neither rooted
        // nor equal to its own file name, and anything else the platform reads as more than a leaf.
        if (!string.Equals(Path.GetFileName(decoded), decoded, StringComparison.Ordinal))
            return MediaIdResult.Escape("not-a-file-name");

        return MediaIdResult.Good(decoded);
    }

    /// <summary>
    /// Decodes <c>%XX</c> escapes over UTF-8 bytes. A byte sequence that is not valid UTF-8 is
    /// refused rather than repaired with U+FFFD, which is why this does not use
    /// <c>Uri.UnescapeDataString</c> (§ 11.1.2: not valid UTF-8 → <c>400 invalid_media_id</c>,
    /// <c>details.reason: "not-utf8"</c>). A '+' stays a literal plus: this is a path segment, not
    /// a query (§ 11.1.1).
    /// </summary>
    private static bool TryPercentDecode(string segment, out string decoded, out string reason)
    {
        decoded = "";
        reason = "";

        var bytes = new List<byte>(segment.Length);
        Span<byte> wide = stackalloc byte[4];
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '%')
            {
                if (i + 2 >= segment.Length || !TryHex(segment[i + 1], out var hi) || !TryHex(segment[i + 2], out var lo))
                {
                    reason = "bad-escape";
                    return false;
                }

                var b = (byte)((hi << 4) | lo);

                // %2F and %5C decode to separators, which an id may never contain (§ 11.1.5).
                if (b == (byte)'/' || b == (byte)'\\')
                {
                    reason = "separator";
                    return false;
                }

                bytes.Add(b);
                i += 2;
            }
            else if (c < 0x80)
            {
                bytes.Add((byte)c);
            }
            else
            {
                // A raw non-ASCII character in the target: not conformant on the client's part,
                // but unambiguous. Take its UTF-8 bytes and carry on.
                var pairLength = char.IsHighSurrogate(c) && i + 1 < segment.Length ? 2 : 1;
                var n = Encoding.UTF8.GetBytes(segment.AsSpan(i, pairLength), wide);
                for (var k = 0; k < n; k++)
                    bytes.Add(wide[k]);
                i += pairLength - 1;
            }
        }

        try
        {
            decoded = StrictUtf8.GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            reason = "not-utf8";
            return false;
        }
    }

    private static bool TryHex(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }

    /// <summary>
    /// Percent-encodes a filename back into one path segment under RFC 3986 path-segment rules, so
    /// the media layer hands out URLs spelled exactly as a snapshot's <c>links</c> spell them
    /// (§ 9.3). Unreserved is <c>A-Z a-z 0-9 - . _ ~</c>; everything else is escaped, a space as
    /// <c>%20</c>.
    /// </summary>
    public static string Encode(string id)
    {
        var sb = new StringBuilder(id.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(id))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }

        return sb.ToString();
    }
}
