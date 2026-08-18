namespace RankMaster2.Ranking;

public sealed class TrueSkill : IRatingEngine
{
    private const double CdfFloor = 1e-10;

    public (Rating Winner, Rating Loser) Update(Rating winner, Rating loser)
    {
        var beta2 = Sq(RankingConstants.Beta);
        var c2 = (2 * beta2) + Sq(winner.Sigma) + Sq(loser.Sigma);
        var c = Math.Sqrt(c2);
        var t = (winner.Mu - loser.Mu) / c;
        var cdf = Math.Max(Cdf(t), CdfFloor);
        var v = Pdf(t) / cdf;
        var w = v * (v + t);

        var winVar = Sq(winner.Sigma);
        var loseVar = Sq(loser.Sigma);

        var winMu = winner.Mu + (winVar / c) * v;
        var loseMu = loser.Mu - (loseVar / c) * v;
        var winSig = Math.Sqrt(Math.Max(winVar * (1 - (winVar / c2) * w), 0));
        var loseSig = Math.Sqrt(Math.Max(loseVar * (1 - (loseVar / c2) * w), 0));

        var tau2 = Sq(RankingConstants.Tau);
        return (
            new Rating(winMu, Math.Sqrt(Sq(winSig) + tau2)),
            new Rating(loseMu, Math.Sqrt(Sq(loseSig) + tau2)));
    }

    public static double MatchQuality(Rating a, Rating b)
    {
        var beta2 = Sq(RankingConstants.Beta);
        var c2 = (2 * beta2) + Sq(a.Sigma) + Sq(b.Sigma);
        var dMu = a.Mu - b.Mu;
        return Math.Sqrt((2 * beta2) / c2) * Math.Exp(-Sq(dMu) / (2 * c2));
    }

    internal static double Pdf(double x) =>
        (1.0 / Math.Sqrt(2 * Math.PI)) * Math.Exp(-0.5 * x * x);

    internal static double Cdf(double x) =>
        0.5 * (1.0 + Erf(x / Math.Sqrt(2)));

    // A&S 7.1.26
    private static double Erf(double x)
    {
        var sign = x >= 0 ? 1.0 : -1.0;
        x = Math.Abs(x);
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;
        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return sign * y;
    }

    private static double Sq(double x) => x * x;
}
