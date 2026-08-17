namespace RankMaster2;

public readonly record struct MediaId(string Filename)
{
    public override string ToString() => Filename;

    public string Extension
    {
        get
        {
            var i = Filename.LastIndexOf('.');
            return i < 0 ? "" : Filename[(i + 1)..].ToLowerInvariant();
        }
    }
}

public enum MediaKind
{
    Still,
    Video
}

public readonly record struct Rating(double Mu, double Sigma)
{
    public double ConservativeScore => Mu - (3.0 * Sigma);
}

public sealed record MediaRecord(
    MediaId Id,
    MediaKind Kind,
    Rating Rating,
    int Matches,
    int Impressions,
    long LastPlayed)
{
    public string Filename => Id.Filename;
}

public readonly record struct Pair(MediaId Left, MediaId Right)
{
    public bool Contains(MediaId id) => Left == id || Right == id;
}

public enum AppScreen
{
    Start,
    Loading,
    Ranking,
    Renaming,
    Saved
}

public static class MediaExtensions
{
    public static readonly HashSet<string> Still = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "bmp", "webp"
    };

    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "mkv", "avi", "mov"
    };

    public static MediaKind? KindOf(string filename)
    {
        var ext = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
        if (Still.Contains(ext)) return MediaKind.Still;
        if (Video.Contains(ext)) return MediaKind.Video;
        return null;
    }
}
