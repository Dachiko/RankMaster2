using RankMaster2;

namespace RankMaster2.Pc.Stills;

/// <summary>
/// <see cref="IMediaProbe"/>: the catalog's eligibility loop (RankMaster2.Catalog.JsonCatalog.
/// ListTopLevelMedia), rewritten here -- names and attributes only, no file bytes read -- so this
/// part does not depend on Catalog (plan section 1, "Dependencies"; section 2.2).
/// </summary>
public sealed class MediaProbe : IMediaProbe
{
    private const string CatalogFileName = "rankmaster_db.json";

    public Task<FolderMedia> ProbeAsync(string folder, CancellationToken ct) =>
        Task.Run(() => Probe(folder, ct), ct);

    private static FolderMedia Probe(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
            return new FolderMedia(0, 0);

        var stills = 0;
        var videos = 0;

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            if (name.Equals(CatalogFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Hidden) != 0 || (attrs & FileAttributes.System) != 0)
                continue;

            switch (MediaExtensions.KindOf(name))
            {
                case MediaKind.Still: stills++; break;
                case MediaKind.Video: videos++; break;
            }
        }

        return new FolderMedia(stills, videos);
    }
}
