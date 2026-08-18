using System.Windows.Media.Imaging;

namespace RankMaster2;

public sealed class PreparedFrame
{
    public required MediaKind Kind { get; init; }
    public BitmapSource? Still { get; init; }
    public string? VideoPath { get; init; }
    public bool IsPreview { get; init; }
}
