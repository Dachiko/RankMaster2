namespace RankMaster2.Ranking;

public static class RecordUpdates
{
    public static (MediaRecord Winner, MediaRecord Loser) ApplyVote(
        IRatingEngine engine,
        MediaRecord winner,
        MediaRecord loser,
        long lastPlayed)
    {
        var (wr, lr) = engine.Update(winner.Rating, loser.Rating);
        return (
            winner with
            {
                Rating = wr,
                Matches = winner.Matches + 1,
                Impressions = winner.Impressions + 1,
                LastPlayed = lastPlayed
            },
            loser with
            {
                Rating = lr,
                Matches = loser.Matches + 1,
                Impressions = loser.Impressions + 1,
                LastPlayed = lastPlayed
            });
    }

    public static MediaRecord ApplySkip(MediaRecord record) =>
        record with { Impressions = record.Impressions + 1 };
}
