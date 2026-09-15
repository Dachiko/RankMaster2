using System.Reflection;
using System.Text.Json.Serialization;
using RankMaster2.Server.Sessions;
using RankMaster2.Server.Tests.Harness;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace RankMaster2.Server.Tests;

/// <summary>
/// <c>openapi.yaml</c> is the contract four client surfaces are written against, and it is edited
/// by hand. Nothing else in this repository reads it, so a broken one stays broken quietly: an
/// indentation slip left it unparseable for a whole commit, and the only reason it surfaced was
/// someone reading it closely.
///
/// <para>These tests do the two things that need no judgement: the document parses, and the shapes
/// it promises still match the records the server actually serialises. A field added to a record
/// and forgotten in the document is a client that rejects a valid response.</para>
/// </summary>
public class OpenApiContractTests
{
    private static YamlMappingNode Document()
    {
        var path = Path.Combine(Repo.Root, "openapi.yaml");
        Assert.True(File.Exists(path), $"openapi.yaml should be at {path}");

        var yaml = new YamlStream();
        using var reader = new StreamReader(path);
        yaml.Load(reader);

        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static YamlMappingNode Schema(string name) =>
        (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)Document()["components"])["schemas"])[name];

    /// <summary>
    /// The wire names of a record. These records write the attribute as
    /// <c>[property: JsonPropertyName("x")]</c>, so it lands on the generated property rather than
    /// on the constructor parameter - reading the parameter finds nothing and silently falls back
    /// to the C# name.
    /// </summary>
    private static IReadOnlyList<string> WireNamesOf<T>() =>
        typeof(T).GetConstructors().First().GetParameters()
            .Select(p =>
                typeof(T).GetProperty(p.Name!)?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? p.Name!)
            .ToList();

    private static IReadOnlyList<string> RequiredOf(string schema) =>
        ((YamlSequenceNode)Schema(schema)["required"]).Select(n => n.ToString()).ToList();

    private static IReadOnlyList<string> PropertiesOf(string schema) =>
        ((YamlMappingNode)Schema(schema)["properties"]).Children.Keys.Select(k => k.ToString()).ToList();

    [Fact]
    public void The_document_parses()
    {
        var root = Document();

        Assert.Equal("3.1.0", root["openapi"].ToString());
        Assert.NotEmpty(((YamlMappingNode)root["paths"]).Children);
    }

    [Theory]
    [InlineData("/session")]
    [InlineData("/session/pair")]
    [InlineData("/session/vote")]
    [InlineData("/session/skip")]
    [InlineData("/session/discard")]
    [InlineData("/session/special")]
    [InlineData("/session/save")]
    [InlineData("/session/undo")]
    [InlineData("/pair")]
    [InlineData("/ping")]
    public void Every_route_the_server_maps_is_in_the_document(string path)
    {
        var paths = (YamlMappingNode)Document()["paths"];

        Assert.True(paths.Children.Keys.Any(k => k.ToString() == path),
            $"SERVER_SPEC.md § 10 maps {path}, and openapi.yaml does not document it. A client " +
            "generated from this document would not know the route exists.");
    }

    /// <summary>
    /// The one that catches drift. <c>SnapshotLastAction</c> gained <c>undoneType</c> when cancel
    /// learned to take back a vote; a document that still promised the old shape would have every
    /// strict client rejecting a valid response.
    /// </summary>
    [Fact]
    public void LastAction_promises_exactly_what_the_server_serialises()
    {
        var served = WireNamesOf<SnapshotLastAction>();

        Assert.Equal(served.OrderBy(n => n), PropertiesOf("LastAction").OrderBy(n => n));
        Assert.Equal(served.OrderBy(n => n), RequiredOf("LastAction").OrderBy(n => n));
    }

    [Fact]
    public void SessionSnapshot_promises_exactly_what_the_server_serialises()
    {
        var served = WireNamesOf<SessionSnapshot>();

        Assert.Equal(served.OrderBy(n => n), PropertiesOf("SessionSnapshot").OrderBy(n => n));
        Assert.Equal(served.OrderBy(n => n), RequiredOf("SessionSnapshot").OrderBy(n => n));
    }

    [Fact]
    public void Counts_and_MediaRef_promise_what_the_server_serialises()
    {
        Assert.Equal(
            WireNamesOf<SnapshotCounts>().OrderBy(n => n),
            PropertiesOf("Counts").OrderBy(n => n));

        Assert.Equal(
            WireNamesOf<SnapshotMediaRef>().OrderBy(n => n),
            PropertiesOf("MediaRef").OrderBy(n => n));
    }
}
