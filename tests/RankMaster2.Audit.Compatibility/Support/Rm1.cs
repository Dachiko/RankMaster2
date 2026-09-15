using System.Globalization;
using System.Text;

namespace RankMaster2.Audit.Compatibility.Support;

/// <summary>
/// Writes <c>rankmaster_db.json</c> the way a file that has been sitting next to a photo library
/// for years actually looks: hand-rolled text, not this server's serialiser.
/// <para/>
/// That is the whole point. If the fixture were produced by <c>JsonCatalog</c> itself, a change to
/// the writer would change the fixture with it and the round-trip would keep passing while the
/// owner's real files stopped opening. So the bytes here are written against SPEC.md § Persistence:
/// four-space indent, literal UTF-8 for non-ASCII names, <c>25.0</c> spelled with its decimal
/// point, and the fields in the order the specification lists them.
/// </summary>
public static class Rm1
{
    public sealed record Row(
        string Filename,
        double Mu,
        double Sigma,
        int Matches,
        int Impressions,
        long LastPlayed);

    /// <summary>A row with the fields a v1 file carries, at values that must survive untouched.</summary>
    public static Row Rated(string filename, double mu, double sigma, int matches, int impressions, long lastPlayed) =>
        new(filename, mu, sigma, matches, impressions, lastPlayed);

    public static void Write(string folder, long lastUpdated, params Row[] rows) =>
        Write(folder, lastUpdated, (IEnumerable<Row>)rows);

    public static void Write(string folder, long lastUpdated, IEnumerable<Row> rows)
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("    \"version\": 1,\n");
        text.Append(CultureInfo.InvariantCulture, $"    \"lastUpdated\": {lastUpdated},\n");
        text.Append("    \"images\": {\n");

        var list = rows.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var row = list[i];
            var key = Escape(row.Filename);
            text.Append(CultureInfo.InvariantCulture, $"        \"{key}\": {{\n");
            text.Append(CultureInfo.InvariantCulture, $"            \"filename\": \"{key}\",\n");
            text.Append("            \"rating\": {\n");
            text.Append(CultureInfo.InvariantCulture, $"                \"mu\": {Number(row.Mu)},\n");
            text.Append(CultureInfo.InvariantCulture, $"                \"sigma\": {Number(row.Sigma)}\n");
            text.Append("            },\n");
            text.Append(CultureInfo.InvariantCulture, $"            \"matches\": {row.Matches},\n");
            text.Append(CultureInfo.InvariantCulture, $"            \"impressions\": {row.Impressions},\n");
            text.Append(CultureInfo.InvariantCulture, $"            \"lastPlayed\": {row.LastPlayed}\n");
            text.Append(i == list.Count - 1 ? "        }\n" : "        },\n");
        }

        text.Append("    }\n");
        text.Append("}\n");

        File.WriteAllText(Db.PathIn(folder), text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// A v1 file from a Rank Master 1 vintage that predates <c>impressions</c>. SPEC.md § Catalog
    /// requires the loader to be tolerant of it and treat the missing field as 0.
    /// </summary>
    public static void WriteWithoutImpressions(string folder, long lastUpdated, params Row[] rows)
    {
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("    \"version\": 1,\n");
        text.Append(CultureInfo.InvariantCulture, $"    \"lastUpdated\": {lastUpdated},\n");
        text.Append("    \"images\": {\n");
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var key = Escape(row.Filename);
            text.Append(CultureInfo.InvariantCulture, $"        \"{key}\": {{\n");
            text.Append(CultureInfo.InvariantCulture, $"            \"filename\": \"{key}\",\n");
            text.Append(CultureInfo.InvariantCulture,
                $"            \"rating\": {{\"mu\": {Number(row.Mu)}, \"sigma\": {Number(row.Sigma)}}},\n");
            text.Append(CultureInfo.InvariantCulture, $"            \"matches\": {row.Matches},\n");
            text.Append(CultureInfo.InvariantCulture, $"            \"lastPlayed\": {row.LastPlayed}\n");
            text.Append(i == rows.Length - 1 ? "        }\n" : "        },\n");
        }
        text.Append("    }\n}\n");

        File.WriteAllText(Db.PathIn(folder), text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>`25.0`, not `25` — the spelling SPEC.md § Persistence shows.</summary>
    private static string Number(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
    }

    private static string Escape(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
