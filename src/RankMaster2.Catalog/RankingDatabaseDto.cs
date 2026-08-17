using System.Text.Json.Serialization;

namespace RankMaster2.Catalog;

internal sealed class RankingDatabaseDto
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("lastUpdated")]
    public long LastUpdated { get; set; }

    [JsonPropertyName("images")]
    public Dictionary<string, ImageRecordDto> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ImageRecordDto
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("rating")]
    public RatingDto Rating { get; set; } = new();

    [JsonPropertyName("matches")]
    public int Matches { get; set; }

    [JsonPropertyName("impressions")]
    public int Impressions { get; set; }

    [JsonPropertyName("lastPlayed")]
    public long LastPlayed { get; set; }
}

internal sealed class RatingDto
{
    [JsonPropertyName("mu")]
    public double Mu { get; set; } = Ranking.RankingConstants.InitialMu;

    [JsonPropertyName("sigma")]
    public double Sigma { get; set; } = Ranking.RankingConstants.InitialSigma;
}
