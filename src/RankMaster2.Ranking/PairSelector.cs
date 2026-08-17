namespace RankMaster2.Ranking;

public sealed class PairSelector : IPairSelector
{
    public Pair? SelectNextPair(IReadOnlyList<MediaRecord> records, IReadOnlySet<MediaId> recentShownIds)
    {
        if (records.Count < 2)
            return null;

        var eligible = records.ToList();
        var available = WithoutRecent(eligible, recentShownIds);
        if (available.Count < 2)
            available = eligible;

        if (available.Count < 2)
            return null;

        var unplaced = available.Where(r => r.Matches < RankingConstants.UnplacedMatchThreshold).ToList();
        var placed = available.Where(r => r.Matches >= RankingConstants.UnplacedMatchThreshold).ToList();

        if (unplaced.Count > 0)
        {
            var p1 = HighestSigma(unplaced);
            MediaRecord? p2;
            if (placed.Count > 0)
                p2 = BestQuality(p1, placed);
            else
                p2 = HighestSigma(unplaced.Where(r => r.Id != p1.Id));

            if (p2 is null)
                return null;
            return new Pair(p1.Id, p2.Id);
        }

        var first = HighestSigma(available);
        var partner = BestUncertainQuality(first, available.Where(r => r.Id != first.Id));
        return partner is null ? null : new Pair(first.Id, partner.Id);
    }

    private static List<MediaRecord> WithoutRecent(
        IReadOnlyList<MediaRecord> records,
        IReadOnlySet<MediaId> recent)
    {
        if (recent.Count == 0)
            return records.ToList();
        return records.Where(r => !recent.Contains(r.Id)).ToList();
    }

    private static MediaRecord HighestSigma(IEnumerable<MediaRecord> records) =>
        records
            .OrderByDescending(r => r.Rating.Sigma)
            .ThenBy(r => r.Filename, StringComparer.OrdinalIgnoreCase)
            .First();

    private static MediaRecord? BestQuality(MediaRecord p1, IEnumerable<MediaRecord> candidates) =>
        candidates
            .Select(p2 => (p2, q: TrueSkill.MatchQuality(p1.Rating, p2.Rating)))
            .OrderByDescending(x => x.q)
            .ThenBy(x => x.p2.Filename, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.p2)
            .FirstOrDefault();

    private static MediaRecord? BestUncertainQuality(MediaRecord p1, IEnumerable<MediaRecord> candidates) =>
        candidates
            .Select(p2 => (p2, q: TrueSkill.MatchQuality(p1.Rating, p2.Rating) * p2.Rating.Sigma))
            .OrderByDescending(x => x.q)
            .ThenBy(x => x.p2.Filename, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.p2)
            .FirstOrDefault();
}
