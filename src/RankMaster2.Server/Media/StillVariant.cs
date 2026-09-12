using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace RankMaster2.Server.Media;

public enum StillFormat
{
    Jpeg,
    Webp,
}

/// <summary>How the output format was decided — only <see cref="FromAccept"/> earns <c>Vary: Accept</c>.</summary>
public enum FormatSource
{
    Default,
    FromQuery,
    FromAccept,
}

/// <summary>
/// One concrete representation of a still: the <b>requested</b> width and the chosen format.
/// SERVER_SPEC.md § 12.2: "<c>{w}</c> in the variant is the requested width, not the delivered
/// one", so two clients asking for different widths of a small image get different ETags even
/// when the bytes match.
/// </summary>
public readonly record struct StillVariant(int RequestedWidth, StillFormat Format, bool IsThumb, FormatSource Source)
{
    /// <summary>The <c>variant</c> token of § 12.2: <c>s1080j</c>, <c>t320w</c>, and so on.</summary>
    public string Token
    {
        get
        {
            var f = Format == StillFormat.Jpeg ? "j" : "w";
            return IsThumb ? "t320" + f : "s" + RequestedWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) + f;
        }
    }

    /// <summary>The width the pixels are actually fitted to. <c>thumb</c> is fixed at 320.</summary>
    public int TargetWidth => IsThumb ? StillWidths.ThumbWidth : RequestedWidth;

    public string ContentType => Format == StillFormat.Jpeg ? "image/jpeg" : "image/webp";

    public string FileExtension => Format == StillFormat.Jpeg ? "jpg" : "webp";
}

public static class StillWidths
{
    /// <summary>§ 12.3. Capping the set is what keeps the on-disk cache bounded.</summary>
    public static readonly int[] Allowed = [360, 540, 720, 1080, 1440, 2160];

    public const int Default = 1080;

    public const int ThumbWidth = 320;

    public static bool IsAllowed(int w) => Array.IndexOf(Allowed, w) >= 0;
}

public enum VariantFault
{
    None,
    UnsupportedWidth,
    UnsupportedFormat,
}

public readonly record struct VariantResult(StillVariant Variant, VariantFault Fault, int RequestedRawWidth)
{
    public bool Ok => Fault == VariantFault.None;
}

public static class StillVariantParser
{
    /// <summary>
    /// § 12.3, in the order the contract states it.
    /// <list type="bullet">
    /// <item><c>w</c> omitted → 1080; any value outside the allowed set → <c>400 unsupported_width</c>,
    /// including a smaller one that "would be fine".</item>
    /// <item><c>thumb</c> ignores <c>w</c> entirely — a <c>w</c> there MUST be ignored, not rejected.</item>
    /// <item>Format precedence: <c>format=</c> wins; else <c>image/webp</c> in <c>Accept</c> with a
    /// non-zero q; else JPEG.</item>
    /// </list>
    /// </summary>
    public static VariantResult Parse(IQueryCollection query, StringValues acceptHeader, bool isThumb)
    {
        var width = StillWidths.Default;
        var rawWidth = 0;

        if (!isThumb && query.TryGetValue("w", out var wValues) && wValues.Count > 0)
        {
            var raw = wValues[^1];
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out rawWidth)
                || !StillWidths.IsAllowed(rawWidth))
            {
                return new VariantResult(default, VariantFault.UnsupportedWidth, rawWidth);
            }

            width = rawWidth;
        }

        StillFormat format;
        FormatSource source;

        if (query.TryGetValue("format", out var formatValues) && formatValues.Count > 0)
        {
            var raw = formatValues[^1];
            switch (raw)
            {
                case "jpeg":
                    format = StillFormat.Jpeg;
                    break;
                case "webp":
                    format = StillFormat.Webp;
                    break;
                default:
                    return new VariantResult(default, VariantFault.UnsupportedFormat, rawWidth);
            }

            source = FormatSource.FromQuery;
        }
        else if (AcceptsWebp(acceptHeader))
        {
            format = StillFormat.Webp;
            source = FormatSource.FromAccept;
        }
        else
        {
            format = StillFormat.Jpeg;
            source = FormatSource.Default;
        }

        return new VariantResult(
            new StillVariant(isThumb ? StillWidths.ThumbWidth : width, format, isThumb, source),
            VariantFault.None,
            rawWidth);
    }

    /// <summary>
    /// § 12.3 step 2: the <c>Accept</c> header "includes <c>image/webp</c> (with a non-zero q)".
    /// Read literally — a wildcard <c>*/*</c> is not an <c>image/webp</c> and does not select WebP,
    /// which keeps JPEG the answer for a client that expressed no preference.
    /// </summary>
    public static bool AcceptsWebp(StringValues acceptHeader)
    {
        foreach (var header in acceptHeader)
        {
            if (string.IsNullOrEmpty(header))
                continue;

            foreach (var part in header.Split(','))
            {
                var span = part.AsSpan().Trim();
                if (span.IsEmpty)
                    continue;

                var semi = span.IndexOf(';');
                var mediaType = (semi < 0 ? span : span[..semi]).Trim();
                if (!mediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (semi >= 0 && QualityIsZero(span[(semi + 1)..]))
                    continue;

                return true;
            }
        }

        return false;
    }

    private static bool QualityIsZero(ReadOnlySpan<char> parameters)
    {
        foreach (var parameter in parameters.ToString().Split(';'))
        {
            var p = parameter.AsSpan().Trim();
            if (!p.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = p[2..].Trim();
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q)
                && q <= 0;
        }

        return false;
    }
}
