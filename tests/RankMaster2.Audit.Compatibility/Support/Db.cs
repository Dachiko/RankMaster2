using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace RankMaster2.Audit.Compatibility.Support;

/// <summary>
/// The user's <c>rankmaster_db.json</c>, read the way the desktop app and Rank Master 1 read it:
/// as text and as JSON, never through the server's own DTOs. A record type would quietly paper
/// over the very things this audit is about — which keys exist, how they are spelled, whether a
/// field was dropped.
/// </summary>
public static class Db
{
    public const string FileName = "rankmaster_db.json";

    /// <summary>The exact top-level shape of SPEC.md § Persistence, in order.</summary>
    public static readonly string[] TopLevelFields = ["version", "lastUpdated", "images"];

    /// <summary>The exact per-image shape of SPEC.md § Persistence, in order.</summary>
    public static readonly string[] RowFields = ["filename", "rating", "matches", "impressions", "lastPlayed"];

    public static readonly string[] RatingFields = ["mu", "sigma"];

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    public static bool Exists(string folder) => File.Exists(PathIn(folder));

    public static string ReadText(string folder) => File.ReadAllText(PathIn(folder), Encoding.UTF8);

    public static byte[] ReadBytes(string folder) => File.ReadAllBytes(PathIn(folder));

    public static JsonElement Read(string folder)
    {
        var path = PathIn(folder);
        Assert.True(File.Exists(path), $"Expected {FileName} in {folder}; the folder holds: " + Listing(folder));
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new XunitException(
                $"{path} does not parse as JSON, so neither the desktop app nor Rank Master 1 can open it.\n" +
                $"  {ex.Message}\n  Content was:\n{Indent(File.ReadAllText(path))}");
        }
    }

    /// <summary>One row of <c>images</c>, with its object key kept alongside its `filename` field.</summary>
    public sealed record Row(
        string Key,
        string Filename,
        double Mu,
        double Sigma,
        int Matches,
        int Impressions,
        long LastPlayed)
    {
        public override string ToString() =>
            $"{Key} -> filename={Filename} mu={Mu:R} sigma={Sigma:R} matches={Matches} impressions={Impressions} lastPlayed={LastPlayed}";
    }

    /// <summary>Every row, keyed by its JSON object key (not by its `filename` field — they can differ).</summary>
    public static Dictionary<string, Row> Rows(string folder)
    {
        var root = Read(folder);
        var images = root.GetProperty("images");
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var property in images.EnumerateObject())
        {
            var value = property.Value;
            rows[property.Name] = new Row(
                property.Name,
                value.GetProperty("filename").GetString() ?? "",
                value.GetProperty("rating").GetProperty("mu").GetDouble(),
                value.GetProperty("rating").GetProperty("sigma").GetDouble(),
                value.GetProperty("matches").GetInt32(),
                value.GetProperty("impressions").GetInt32(),
                value.GetProperty("lastPlayed").GetInt64());
        }

        return rows;
    }

    public static Row Row1(string folder, string key)
    {
        var rows = Rows(folder);
        Assert.True(rows.TryGetValue(key, out var row),
            $"{FileName} has no row keyed '{key}'. It holds: {string.Join(", ", rows.Keys)}");
        return row!;
    }

    /// <summary>
    /// SPEC.md § Persistence, clause by clause: version 1, the filename as both key and field, no
    /// new fields, no dropped fields, pretty-printed.
    /// </summary>
    public static void RequireV1Schema(string folder, string clause)
    {
        var text = ReadText(folder);
        var root = Read(folder);

        Assert.True(root.ValueKind == JsonValueKind.Object,
            $"{clause}: the ranking file must be a JSON object, not {root.ValueKind}.");

        var top = root.EnumerateObject().Select(p => p.Name).ToArray();
        AssertSameFields(TopLevelFields, top,
            $"{clause}: the v1 top level is exactly {{version, lastUpdated, images}} (SPEC.md § Persistence)");

        Assert.True(root.GetProperty("version").ValueKind == JsonValueKind.Number,
            $"{clause}: `version` must be a number.");
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        Assert.True(root.GetProperty("lastUpdated").ValueKind == JsonValueKind.Number,
            $"{clause}: `lastUpdated` must be a number.");

        var images = root.GetProperty("images");
        Assert.True(images.ValueKind == JsonValueKind.Object,
            $"{clause}: `images` must be an object keyed by filename, not {images.ValueKind}.");

        foreach (var property in images.EnumerateObject())
        {
            var where = $"{clause}: images[\"{property.Name}\"]";
            Assert.True(property.Value.ValueKind == JsonValueKind.Object, $"{where} must be an object.");

            AssertSameFields(RowFields, property.Value.EnumerateObject().Select(p => p.Name).ToArray(),
                $"{where}: a v1 row is exactly {{filename, rating, matches, impressions, lastPlayed}}");

            var rating = property.Value.GetProperty("rating");
            Assert.True(rating.ValueKind == JsonValueKind.Object, $"{where}.rating must be an object.");
            AssertSameFields(RatingFields, rating.EnumerateObject().Select(p => p.Name).ToArray(),
                $"{where}.rating is exactly {{mu, sigma}}");

            var filename = property.Value.GetProperty("filename").GetString();
            Assert.True(string.Equals(property.Name, filename, StringComparison.Ordinal),
                $"{where}: SPEC.md § Persistence — \"the object key and the `filename` field must match\", " +
                $"but the key is '{property.Name}' and the field is '{filename}'.");

            Assert.True(property.Value.GetProperty("matches").ValueKind == JsonValueKind.Number, $"{where}.matches must be a number.");
            Assert.True(property.Value.GetProperty("impressions").ValueKind == JsonValueKind.Number, $"{where}.impressions must be a number.");
            Assert.True(property.Value.GetProperty("lastPlayed").ValueKind == JsonValueKind.Number, $"{where}.lastPlayed must be a number.");
        }

        Assert.True(text.Contains('\n'),
            $"{clause}: SPEC.md § Persistence requires pretty-printed JSON (Indent = true); this file is one line.");
        Assert.True(text.Contains("\n  \"version\""),
            $"{clause}: the file is not indented the way `WriteIndented` writes it. First 200 chars:\n{Indent(text[..Math.Min(200, text.Length)])}");
    }

    private static void AssertSameFields(string[] expected, string[] actual, string clause)
    {
        var missing = expected.Except(actual, StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && extra.Length == 0)
            return;

        var report = new StringBuilder(clause).Append(".\n");
        if (missing.Length > 0) report.Append("  dropped fields: ").Append(string.Join(", ", missing)).Append('\n');
        if (extra.Length > 0) report.Append("  new fields:     ").Append(string.Join(", ", extra)).Append('\n');
        report.Append("  got: ").Append(string.Join(", ", actual));
        throw new XunitException(report.ToString());
    }

    /// <summary>
    /// The file with the two things about it that are legitimately not reproducible removed: the
    /// wall-clock <c>lastUpdated</c>, and the order of <c>images</c>, which follows directory
    /// enumeration order. Everything a golden file is for — indentation, escaping, number
    /// spelling, field order inside a row, which fields exist at all — survives untouched, because
    /// each value is re-emitted from its own parsed element rather than reformatted.
    /// </summary>
    public static string Canonicalise(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("lastUpdated"))
                {
                    writer.WriteNumber("lastUpdated", 0);
                }
                else if (property.NameEquals("images") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("images");
                    writer.WriteStartObject();
                    foreach (var row in property.Value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                        row.WriteTo(writer);
                    writer.WriteEndObject();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).ReplaceLineEndings("\n");
    }

    public static string Listing(string folder) =>
        Directory.Exists(folder)
            ? string.Join(", ", Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order())
            : "(the folder does not exist)";

    private static string Indent(string text) =>
        string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line => "    " + line));
}
