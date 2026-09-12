namespace RankMaster2.Server.Security;

public enum PathRejection
{
    None,
    Empty,
    TooLong,
    Nul,
    ControlCharacter,
    NotAbsolute,
    Traversal,
    Unc,
    DeviceName,
    TrailingDotOrSpace,
    Malformed,
}

public sealed record PathCheck(bool Ok, string Canonical, PathRejection Reason)
{
    public static PathCheck Rejected(PathRejection reason) => new(false, "", reason);
}

/// <summary>
/// Validation and canonicalisation for every caller-supplied filesystem path.
///
/// <para>
/// Browsing is unrestricted by product decision (SERVER_SPEC.md § 10.15), so this class is not a
/// jail — there is no root to stay inside. What it does is make the path the server acts on
/// <b>exactly the path the caller asked for</b>, with no form of the request that means one thing to
/// this validator and another to the operating system. Every rule below exists because some pair of
/// layers disagrees about what a string means:
/// </para>
/// <list type="bullet">
/// <item><b>Traversal.</b> <c>..</c> and <c>.</c> segments are rejected outright rather than
///   normalised away, before and again after resolution. Nothing legitimate sends them — the parent
///   folder is handed back in <c>parent</c> — and accepting them means the string that is logged and
///   the path that is opened are different strings.</item>
/// <item><b>Encoded traversal.</b> The framework percent-decodes the query string exactly once
///   before this runs, so <c>%2e%2e%2f</c> arrives here as <c>../</c> and is caught by the rule
///   above. Double encoding (<c>%252e</c>) arrives as the literal text <c>%2e</c>, which is a
///   filename character and no longer a traversal at all. This layer never decodes anything a
///   second time — a second decode is how a validator and a filesystem end up disagreeing.</item>
/// <item><b>UNC.</b> <c>\\host\share</c> is refused. This is not squeamishness: on Windows, touching
///   a UNC path makes the server process authenticate to a host of the attacker's choosing, handing
///   over an NTLM exchange from whatever account the server runs as. Mapped drives still work; an
///   arbitrary remote host does not. <c>\\?\</c> and <c>\\.\</c> are refused with it, because they
///   turn off the very normalisation this method depends on.</item>
/// <item><b>Device names.</b> <c>CON</c>, <c>NUL</c>, <c>COM1</c>… resolve to devices rather than
///   files on Windows, with or without an extension. Refused on every platform, so a Linux server
///   and a Windows server answer the same request the same way.</item>
/// <item><b>Trailing dots and spaces.</b> Windows silently strips them, so <c>C:\secret.</c> and
///   <c>C:\secret</c> are the same object to the OS and two different strings to any check written
///   in terms of equality. Refused on every platform for the same reason.</item>
/// <item><b>Resolve, then verify.</b> The checks are re-run on the canonical form. A check that only
///   ever looked at the string the caller sent is a check that can be walked around by making
///   resolution change the string.</item>
/// </list>
/// </summary>
public static class PathGuard
{
    /// <summary>SERVER_SPEC.md § 15: <c>folder</c> / <c>path</c> is capped at 4096 characters.</summary>
    public const int MaxLength = 4096;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static PathCheck Check(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return PathCheck.Rejected(PathRejection.Empty);

        if (raw.Length > MaxLength)
            return PathCheck.Rejected(PathRejection.TooLong);

        foreach (var c in raw)
        {
            if (c == '\0') return PathCheck.Rejected(PathRejection.Nul);
            if (char.IsControl(c)) return PathCheck.Rejected(PathRejection.ControlCharacter);
        }

        // UNC and Win32 device namespaces, on every platform. A Linux server must not be a
        // convenient proxy for reaching a Windows client's idea of \\host\share either.
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            return PathCheck.Rejected(PathRejection.Unc);
        if (raw.Contains(@"\\?\", StringComparison.Ordinal) || raw.Contains(@"\\.\", StringComparison.Ordinal))
            return PathCheck.Rejected(PathRejection.Unc);

        // Fully qualified, not merely "rooted": on Windows `C:photos` is rooted and resolves against
        // a hidden per-drive working directory, and `\photos` is rooted and resolves against the
        // current drive. Neither is a path the caller can be said to have named.
        if (!Path.IsPathFullyQualified(raw))
            return PathCheck.Rejected(PathRejection.NotAbsolute);

        var segmentCheck = CheckSegments(raw);
        if (segmentCheck != PathRejection.None)
            return PathCheck.Rejected(segmentCheck);

        string canonical;
        try
        {
            canonical = Path.GetFullPath(raw);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return PathCheck.Rejected(PathRejection.Malformed);
        }

        if (string.IsNullOrEmpty(canonical) || canonical.Length > MaxLength)
            return PathCheck.Rejected(PathRejection.TooLong);

        // Verify after resolution, not only before it.
        if (!Path.IsPathFullyQualified(canonical))
            return PathCheck.Rejected(PathRejection.NotAbsolute);
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal))
            return PathCheck.Rejected(PathRejection.Unc);

        var afterCheck = CheckSegments(canonical);
        if (afterCheck != PathRejection.None)
            return PathCheck.Rejected(afterCheck);

        return new PathCheck(true, TrimTrailingSeparator(canonical), PathRejection.None);
    }

    private static PathRejection CheckSegments(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var body = path.Length > root.Length ? path[root.Length..] : "";

        foreach (var range in SplitSegments(body))
        {
            var segment = body[range];
            if (segment.Length == 0) continue;

            if (segment is "." or "..")
                return PathRejection.Traversal;

            if (segment[^1] == '.' || segment[^1] == ' ')
                return PathRejection.TrailingDotOrSpace;

            var stem = segment;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            if (stem.Length > 0 && ReservedDeviceNames.Contains(stem.Trim()))
                return PathRejection.DeviceName;
        }

        return PathRejection.None;
    }

    private static IEnumerable<Range> SplitSegments(string body)
    {
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] is not ('/' or '\\')) continue;
            yield return new Range(start, i);
            start = i + 1;
        }

        yield return new Range(start, body.Length);
    }

    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        if (path.Length <= root.Length) return path;

        var end = path.Length;
        while (end > root.Length && (path[end - 1] == '/' || path[end - 1] == '\\')) end--;
        return end == path.Length ? path : path[..end];
    }

    /// <summary>
    /// The parent folder of a canonical path, or <c>null</c> at a root (SERVER_SPEC.md § 10.15).
    /// Computed from the canonical string rather than by appending <c>..</c>, so the answer cannot
    /// be steered by a symlink between the check and the use.
    /// </summary>
    public static string? ParentOf(string canonical)
    {
        var root = Path.GetPathRoot(canonical);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(TrimTrailingSeparator(canonical), TrimTrailingSeparator(root), StringComparison.Ordinal))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(canonical);
        return string.IsNullOrEmpty(parent) ? null : TrimTrailingSeparator(parent);
    }
}
