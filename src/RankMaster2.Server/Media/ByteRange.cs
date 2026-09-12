using System.Globalization;

namespace RankMaster2.Server.Media;

public enum RangeOutcome
{
    /// <summary>No <c>Range</c> header, or one that is syntactically invalid. RFC 9110: serve 200 whole.</summary>
    NotRequested,

    /// <summary>A range that can be served → 206.</summary>
    Satisfiable,

    /// <summary>A well-formed range that the representation cannot satisfy → 416.</summary>
    Unsatisfiable,
}

public readonly record struct ByteRange(long From, long To)
{
    public long Length => To - From + 1;
}

public readonly record struct RangeResult(RangeOutcome Outcome, ByteRange Range)
{
    public static readonly RangeResult None = new(RangeOutcome.NotRequested, default);
    public static readonly RangeResult Unsatisfiable = new(RangeOutcome.Unsatisfiable, default);

    public static RangeResult Ok(long from, long to) => new(RangeOutcome.Satisfiable, new ByteRange(from, to));
}

/// <summary>
/// SERVER_SPEC.md § 12.4 and RFC 9110 § 14.
/// <para/>
/// The distinction that matters, and the one that is easy to get backwards: a <b>syntactically
/// invalid</b> <c>Range</c> is ignored and the whole body is served with 200, while a
/// <b>well-formed but unsatisfiable</b> one is 416 with <c>Content-Range: bytes */size</c>. A
/// zero-length file makes every byte range unsatisfiable, so <c>bytes=0-</c> on an empty file is
/// a 416 even though it looks like a request for everything.
/// </summary>
public static class RangeParser
{
    /// <summary>
    /// Parses one <c>Range</c> header against a representation of <paramref name="length"/> bytes.
    /// Several ranges are allowed on the wire; only the first is ever served, as a single 206
    /// (§ 12.4: "Multipart byte ranges MUST NOT be produced").
    /// </summary>
    public static RangeResult Parse(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header))
            return RangeResult.None;

        var value = header.AsSpan().Trim();
        const string unit = "bytes=";
        if (!value.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
            return RangeResult.None; // An unknown range unit is ignored, not refused.

        var specs = value[unit.Length..].ToString().Split(',');

        // Every spec has to parse before any of them is honoured: a malformed member makes the
        // whole header invalid, and an invalid header is ignored rather than refused.
        var parsed = new List<(long? First, long? Last, long? Suffix)>(specs.Length);
        foreach (var raw in specs)
        {
            var spec = raw.AsSpan().Trim();
            if (spec.IsEmpty)
                return RangeResult.None;

            var dash = spec.IndexOf('-');
            if (dash < 0)
                return RangeResult.None;

            var firstText = spec[..dash].Trim();
            var lastText = spec[(dash + 1)..].Trim();

            if (firstText.IsEmpty)
            {
                // Suffix form: bytes=-N, the last N bytes.
                if (!TryParseNonNegative(lastText, out var suffix))
                    return RangeResult.None;

                parsed.Add((null, null, suffix));
                continue;
            }

            if (!TryParseNonNegative(firstText, out var first))
                return RangeResult.None;

            if (lastText.IsEmpty)
            {
                // Open-ended form: bytes=N-, from N to the end.
                parsed.Add((first, null, null));
                continue;
            }

            if (!TryParseNonNegative(lastText, out var last))
                return RangeResult.None;

            // last < first is an invalid byte-range-spec, so the header is invalid.
            if (last < first)
                return RangeResult.None;

            parsed.Add((first, last, null));
        }

        if (parsed.Count == 0)
            return RangeResult.None;

        var (f, l, s) = parsed[0];

        if (s is { } suffixLength)
        {
            // A suffix of zero bytes names nothing, and a suffix of any size against an empty
            // representation names nothing either. Both are unsatisfiable.
            if (suffixLength == 0 || length == 0)
                return RangeResult.Unsatisfiable;

            var from = Math.Max(0, length - suffixLength);
            return RangeResult.Ok(from, length - 1);
        }

        var start = f!.Value;

        // A first-byte-pos at or past the end is unsatisfiable; on an empty file that is every
        // request, including bytes=0-.
        if (start >= length)
            return RangeResult.Unsatisfiable;

        var end = l is { } lastByte ? Math.Min(lastByte, length - 1) : length - 1;
        return RangeResult.Ok(start, end);
    }

    private static bool TryParseNonNegative(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        if (text.IsEmpty || text.Length > 19)
            return false;

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The <c>Content-Range</c> of a 206.</summary>
    public static string ContentRange(ByteRange range, long length) =>
        string.Create(CultureInfo.InvariantCulture, $"bytes {range.From}-{range.To}/{length}");

    /// <summary>The <c>Content-Range</c> a 416 must carry (§ 12.4).</summary>
    public static string UnsatisfiedContentRange(long length) =>
        string.Create(CultureInfo.InvariantCulture, $"bytes */{length}");
}
