namespace RankMaster2.Ranking;

public static class RankingConstants
{
    public const double InitialMu = 25.0;
    public const double InitialSigma = 8.333;
    public const double Beta = 4.167;
    public const double Tau = 0.083;
    public const double TargetSigma = 1.8;
    public const int UnplacedMatchThreshold = 3;
    public const int RecentShownLimit = 30;
    public const int MatchCueLimit = 10;

    public static Rating DefaultRating => new(InitialMu, InitialSigma);
}
