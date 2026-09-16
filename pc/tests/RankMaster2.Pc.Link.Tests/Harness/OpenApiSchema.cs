using System.Text.RegularExpressions;

namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>
/// Reads the request-body property names for one <c>components.schemas</c> entry straight out of
/// <c>openapi.yaml</c>'s text (W5, § 6.3). A tiny line-based scan rather than a YAML library: the
/// plan's test project has no dependency beyond the xunit SDK (§ 6.1), and every schema this reads is
/// a flat <c>properties:</c> map with no nested objects.
/// </summary>
internal static class OpenApiSchema
{
    private static readonly Lazy<string[]> Lines = new(() => File.ReadAllLines(Path.Combine(Repo.Root, "openapi.yaml")));

    /// <summary>Every property name declared directly under <c>{schemaName}: properties:</c>.</summary>
    public static HashSet<string> PropertyNames(string schemaName)
    {
        var lines = Lines.Value;
        var schemaHeader = new Regex($@"^    {Regex.Escape(schemaName)}:\s*$");
        var propertiesHeader = new Regex(@"^      properties:\s*$");
        var propertyLine = new Regex(@"^        ([A-Za-z0-9_]+):");
        var dedent = new Regex(@"^(    \S| {0,3}\S)"); // a line at 4 spaces or fewer (a new top-level key), or none

        var i = 0;
        while (i < lines.Length && !schemaHeader.IsMatch(lines[i])) i++;
        if (i >= lines.Length)
            throw new InvalidOperationException($"Schema '{schemaName}' not found in openapi.yaml.");

        var schemaStart = i + 1;
        var schemaEnd = schemaStart;
        while (schemaEnd < lines.Length && !dedent.IsMatch(lines[schemaEnd])) schemaEnd++;

        var result = new HashSet<string>();
        var inProperties = false;
        for (var j = schemaStart; j < schemaEnd; j++)
        {
            if (propertiesHeader.IsMatch(lines[j])) { inProperties = true; continue; }
            if (!inProperties) continue;

            // Leaving the properties block: a line at 6 spaces or less that isn't itself "properties:".
            if (Regex.IsMatch(lines[j], @"^      \S") && !propertiesHeader.IsMatch(lines[j])) break;

            var match = propertyLine.Match(lines[j]);
            if (match.Success) result.Add(match.Groups[1].Value);
        }

        if (result.Count == 0)
            throw new InvalidOperationException($"No properties found for schema '{schemaName}' in openapi.yaml.");

        return result;
    }
}
