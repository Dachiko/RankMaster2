namespace RankMaster2.Ranking;

public static class LibraryProgress
{
    /// <summary>
    /// Mean fraction of the path from initial σ to target σ. Used only for the overlay bar.
    /// </summary>
    public static double Of(IEnumerable<MediaRecord> records)
    {
        var travel = RankingConstants.InitialSigma - RankingConstants.TargetSigma;
        if (travel <= 0)
            return 0;

        var n = 0;
        var sum = 0.0;
        foreach (var record in records)
        {
            n++;
            var stepped = RankingConstants.InitialSigma - record.Rating.Sigma;
            sum += Math.Clamp(stepped / travel, 0, 1);
        }

        return n == 0 ? 0 : sum / n;
    }
}
