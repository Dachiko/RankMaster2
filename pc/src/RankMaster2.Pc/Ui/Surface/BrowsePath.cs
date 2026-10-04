namespace RankMaster2.Pc.Ui.Surface;

/// <summary>One piece of the browser's path line: what it says and the folder a click on it goes to
/// (plan I § 2.2 item 7). The drive <c>C:</c> goes to <c>C:\</c>.</summary>
public sealed record PathSegment(string Text, string Target);

/// <summary>The path line after the "too long for the column" rule (plan I § 2.2 item 2): the segments
/// that are shown, and whether earlier ones were dropped behind <c>…\</c>. The last segment (<b>here</b>)
/// is always in <see cref="Shown"/>.</summary>
public sealed record ElidedPath(IReadOnlyList<PathSegment> Shown, bool Elided);

/// <summary>
/// Pure path arithmetic for the folder browser, working on the strings the server gives (Windows drive
/// paths and UNC shares; a <c>/</c> path is understood too, so the model can be tested anywhere). It
/// does not touch the file system and does not use <see cref="System.IO.Path"/>, which would rewrite
/// separators.
/// </summary>
public static class BrowsePath
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>The separator the path was written with, for drawing the path line: <c>\</c> unless the path only
    /// uses <c>/</c>.</summary>
    public static char SeparatorOf(string path) =>
        path.Contains('\\') || (path.Length >= 2 && path[1] == ':') || !path.Contains('/') ? '\\' : '/';

    /// <summary>
    /// <c>C:\images\garden</c> → <c>C:</c>, <c>images</c>, <c>garden</c>, each with the path that reaches it.
    /// A share <c>\\nas\media\x</c> starts with the one segment <c>\\nas\media</c>. Empty path → no segments.
    /// </summary>
    public static IReadOnlyList<PathSegment> Split(string path)
    {
        if (string.IsNullOrEmpty(path)) return [];
        var sep = SeparatorOf(path);
        var segments = new List<PathSegment>();
        string[] parts;
        string accumulated;

        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            var pieces = path[2..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length == 0) return [new PathSegment(path, path)];
            var head = pieces.Length >= 2 ? "\\\\" + pieces[0] + "\\" + pieces[1] : "\\\\" + pieces[0];
            accumulated = head + sep;
            segments.Add(new PathSegment(head, accumulated));
            parts = pieces.Length > 2 ? pieces[2..] : [];
        }
        else if (path.Length >= 2 && path[1] == ':')
        {
            var head = path[..2];
            accumulated = head + sep;
            segments.Add(new PathSegment(head, accumulated));
            parts = path[2..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        }
        else if (path[0] is '/' or '\\')
        {
            accumulated = sep.ToString();
            segments.Add(new PathSegment(accumulated, accumulated));
            parts = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            accumulated = "";
            parts = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        }

        foreach (var part in parts)
        {
            accumulated = accumulated.Length == 0 || accumulated[^1] == sep ? accumulated + part : accumulated + sep + part;
            segments.Add(new PathSegment(part, accumulated));
        }
        return segments;
    }

    /// <summary>The folder one level up, or null at a drive, a share or <c>/</c> (the browser's next step up
    /// from there is THIS PC).</summary>
    public static string? ParentOf(string path)
    {
        var segments = Split(path);
        return segments.Count >= 2 ? segments[^2].Target : null;
    }

    /// <summary>Same folder: compared without a trailing separator, without regard to case, and with <c>/</c> and
    /// <c>\</c> as one.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normal(string path)
    {
        var trimmed = path.Replace('/', '\\').TrimEnd('\\');
        return trimmed.Length == 0 ? "\\" : trimmed;
    }

    /// <summary>A root's row text: the drive or share without its trailing separator (<c>C:\</c> → <c>C:</c>).</summary>
    public static string RootName(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        return trimmed.Length > 0 ? trimmed : path;
    }

    /// <summary>
    /// Plan I § 2.2 item 2: drops the earliest segments, one at a time, until <paramref name="fits"/> says the
    /// line fits the column; the last segment always stays. The caller decides what "fits" means (the view
    /// measures the real text; a test counts characters, see <see cref="Length"/>).
    /// </summary>
    public static ElidedPath Elide(IReadOnlyList<PathSegment> all, Func<ElidedPath, bool> fits)
    {
        if (all.Count == 0) return new ElidedPath(all, false);
        for (var drop = 0; drop < all.Count - 1; drop++)
        {
            var candidate = new ElidedPath(all.Skip(drop).ToList(), drop > 0);
            if (fits(candidate)) return candidate;
        }
        return new ElidedPath([all[^1]], all.Count > 1);
    }

    /// <summary>The line's length in characters: <c>…\</c> if elided, each segment, a separator after each but the last.</summary>
    public static int Length(ElidedPath path) =>
        (path.Elided ? 2 : 0) + path.Shown.Sum(s => s.Text.Length) + Math.Max(0, path.Shown.Count - 1);
}
