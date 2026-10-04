using System.Text.Json.Serialization;

namespace RankMaster2.Pc.Link.Wire;

/// <summary>
/// SERVER_SPEC.md § 10.14, one entry of <c>GET /libraries/roots</c>: a drive (Windows) or mount point.
/// Only the fields the in-app folder browser uses (pc/plans/I-folder-browser.md § 2.1: names, no
/// sizes); the byte counts on the wire are ignored. A root that is listed but not
/// <see cref="Available"/> (an empty drive, a disconnected share) still arrives, and is shown dimmed.
/// </summary>
public sealed record LibraryRoot(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("available")] bool Available);

/// <summary>
/// SERVER_SPEC.md § 10.15, the body of <c>GET /libraries/browse</c>: the direct child folders of
/// <see cref="Path"/>. <see cref="Parent"/> is <c>null</c> at a root.
/// </summary>
public sealed record FolderListing(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("parent")] string? Parent,
    [property: JsonPropertyName("entries")] IReadOnlyList<FolderEntry> Entries);

/// <summary>
/// One child folder in a <see cref="FolderListing"/>. The browser shows only the name; the flags
/// decide whether the row is dimmed. <see cref="Rankable"/> is <c>null</c> when the server did not
/// count (an inaccessible child, or <c>counts=false</c>): unknown, never "no". Counts on the wire are
/// not carried (plan I § 2.1).
/// </summary>
public sealed record FolderEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("rankable")] bool? Rankable,
    [property: JsonPropertyName("hasDatabase")] bool HasDatabase,
    [property: JsonPropertyName("accessible")] bool Accessible);
