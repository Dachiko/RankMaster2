using System.Text.Json;

namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// The exact object shapes of openapi.yaml, which sets <c>additionalProperties: false</c> on every
/// one of them and lists every nullable field as required.
///
/// That combination is deliberate and worth enforcing: "required but nullable" means the key is
/// always present, so a client can read <c>lastAction.restoredId</c> without first asking whether
/// the key exists. A server that omits nulls instead of emitting them is a different API, and four
/// client implementations would each discover that separately.
/// </summary>
public static class ContractShape
{
    public static readonly string[] SnapshotKeys =
    {
        "sessionId", "state", "folder", "folderName", "policy", "openedAt", "prefetchPairs",
        "sessionVotes", "counts", "progress", "progressPercent", "cues", "pair", "pairToken",
        "pairSeq", "warmPairs", "undoAvailable", "lastAction", "lastSavedAt"
    };

    public static readonly string[] CountsKeys = { "total", "rankable", "unranked", "stills", "videos" };
    public static readonly string[] PairKeys = { "left", "right" };
    public static readonly string[] MediaRefKeys = { "id", "kind", "sizeBytes", "mediaVersion", "links" };
    public static readonly string[] LinksKeys = { "meta", "still", "thumb", "video" };

    public static readonly string[] LastActionKeys =
    {
        "seq", "type", "pairToken", "clientRequestId", "winner", "side", "id", "restoredId", "at"
    };

    public static readonly string[] MediaMetaKeys =
    {
        "id", "kind", "sizeBytes", "modifiedAt", "mediaVersion", "width", "height", "rating",
        "matches", "impressions", "lastPlayed", "rankable"
    };

    public static readonly string[] RatingKeys = { "mu", "sigma", "conservative" };

    public static readonly string[] PingKeys =
    {
        "product", "apiVersion", "version", "ready", "authenticated", "certificateFingerprint",
        "serverTime", "features", "limits", "session"
    };

    public static readonly string[] PingFeatureKeys =
    {
        "rename", "videoTranscoding", "posterFrames", "videoProbe", "browse", "maxConcurrentSessions"
    };

    public static readonly string[] PingLimitKeys =
    {
        "stillWidths", "thumbWidth", "maxJsonBodyBytes", "sessionLockTimeoutSeconds"
    };

    public static readonly string[] PingSessionKeys = { "open", "sessionId", "folder", "state" };
    public static readonly string[] PairResponseKeys = { "deviceId", "deviceName", "token", "issuedAt", "expiresAt" };
    public static readonly string[] BrowseResponseKeys = { "path", "parent", "entries" };

    public static readonly string[] BrowseEntryKeys =
    {
        "name", "path", "stillCount", "videoCount", "rankable", "hasDatabase", "accessible"
    };

    public static readonly string[] RootEntryKeys =
    {
        "path", "label", "kind", "available", "totalBytes", "freeBytes"
    };

    // ---- validation -------------------------------------------------------------------------

    public static void RequireSnapshot(JsonElement snapshot, string clause, Rm2Response response)
    {
        RequireExactKeys(snapshot, "SessionSnapshot", SnapshotKeys, clause, response);
        RequireExactKeys(snapshot.GetProperty("counts"), "Counts", CountsKeys, clause, response);

        var state = snapshot.GetProperty("state").GetString();
        if (state is not ("ranking" or "exhausted"))
            throw response.Failure($"{clause}: state must be 'ranking' or 'exhausted', got '{state}'.");

        var pair = snapshot.GetProperty("pair");
        var token = snapshot.GetProperty("pairToken");

        // § 9.1: pairToken is null iff pair is null, and both are null exactly when exhausted.
        if (pair.ValueKind == JsonValueKind.Null != (token.ValueKind == JsonValueKind.Null))
            throw response.Failure(
                $"{clause}: pairToken is null iff pair is null (SERVER_SPEC.md § 9.1). " +
                $"Here pair is {pair.ValueKind} and pairToken is {token.ValueKind}.");

        if ((state == "exhausted") != (pair.ValueKind == JsonValueKind.Null))
            throw response.Failure(
                $"{clause}: state 'exhausted' and a null pair must agree (SERVER_SPEC.md § 7.1). " +
                $"Here state is '{state}' and pair is {pair.ValueKind}.");

        if (pair.ValueKind == JsonValueKind.Object) RequirePair(pair, "pair", clause, response);

        var warm = snapshot.GetProperty("warmPairs");
        if (warm.ValueKind != JsonValueKind.Array)
            throw response.Failure($"{clause}: warmPairs must be an array, even when empty (SERVER_SPEC.md § 9.5).");

        var index = 0;
        foreach (var warmPair in warm.EnumerateArray())
            RequirePair(warmPair, $"warmPairs[{index++}]", clause, response);

        if (index > snapshot.GetProperty("prefetchPairs").GetInt32())
            throw response.Failure($"{clause}: warmPairs has {index} entries but prefetchPairs is " +
                                   $"{snapshot.GetProperty("prefetchPairs").GetInt32()} (SERVER_SPEC.md § 9.1).");

        var cues = snapshot.GetProperty("cues");
        if (cues.ValueKind != JsonValueKind.Array)
            throw response.Failure($"{clause}: cues must be an array.");
        if (cues.GetArrayLength() > 10)
            throw response.Failure($"{clause}: cues holds at most 10 entries (SERVER_SPEC.md § 9.1), got {cues.GetArrayLength()}.");
        foreach (var cue in cues.EnumerateArray())
            if (cue.GetString() is not ("confirmation" or "upset"))
                throw response.Failure($"{clause}: cue must be 'confirmation' or 'upset', got '{cue.GetString()}'.");

        var lastAction = snapshot.GetProperty("lastAction");
        if (lastAction.ValueKind == JsonValueKind.Object)
            RequireExactKeys(lastAction, "LastAction", LastActionKeys, clause, response);

        // § 9.2: the counts have to add up, or the overlay numbers are meaningless.
        var counts = snapshot.GetProperty("counts");
        var total = counts.GetProperty("total").GetInt32();
        var stills = counts.GetProperty("stills").GetInt32();
        var videos = counts.GetProperty("videos").GetInt32();
        if (stills + videos != total)
            throw response.Failure($"{clause}: counts.stills ({stills}) + counts.videos ({videos}) must equal " +
                                   $"counts.total ({total}) (SERVER_SPEC.md § 9.2).");

        var policy = snapshot.GetProperty("policy").GetString();
        var rankable = counts.GetProperty("rankable").GetInt32();
        var expectedRankable = policy == "still" ? stills : videos;
        if (rankable != expectedRankable)
            throw response.Failure($"{clause}: policy is '{policy}', so counts.rankable must equal " +
                                   $"counts.{(policy == "still" ? "stills" : "videos")} ({expectedRankable}), " +
                                   $"got {rankable} (SERVER_SPEC.md § 9.2).");

        var progress = snapshot.GetProperty("progress").GetDouble();
        if (progress is < 0 or > 1)
            throw response.Failure($"{clause}: progress must be within 0…1, got {progress}.");

        var percent = snapshot.GetProperty("progressPercent").GetInt32();
        var expectedPercent = (int)Math.Round(progress * 100, MidpointRounding.AwayFromZero);
        if (Math.Abs(percent - expectedPercent) > 1)
            throw response.Failure($"{clause}: progressPercent must be round(progress × 100) " +
                                   $"(SERVER_SPEC.md § 9.1); progress {progress} implies {expectedPercent}, got {percent}.");
    }

    public static void RequirePair(JsonElement pair, string where, string clause, Rm2Response response)
    {
        RequireExactKeys(pair, where, PairKeys, clause, response);
        var left = pair.GetProperty("left");
        var right = pair.GetProperty("right");
        RequireMediaRef(left, $"{where}.left", clause, response);
        RequireMediaRef(right, $"{where}.right", clause, response);

        if (left.GetProperty("id").GetString() == right.GetProperty("id").GetString())
            throw response.Failure($"{clause}: {where}.left and {where}.right are the same id " +
                                   "— PairSelector never returns an id twice (SERVER_SPEC.md § 9.3).");
    }

    public static void RequireMediaRef(JsonElement reference, string where, string clause, Rm2Response response)
    {
        RequireExactKeys(reference, where, MediaRefKeys, clause, response);
        RequireExactKeys(reference.GetProperty("links"), $"{where}.links", LinksKeys, clause, response);

        var kind = reference.GetProperty("kind").GetString();
        if (kind is not ("still" or "video"))
            throw response.Failure($"{clause}: {where}.kind must be 'still' or 'video', got '{kind}'.");

        var links = reference.GetProperty("links");
        var still = links.GetProperty("still").ValueKind != JsonValueKind.Null;
        var thumb = links.GetProperty("thumb").ValueKind != JsonValueKind.Null;
        var video = links.GetProperty("video").ValueKind != JsonValueKind.Null;

        // § 9.3: a video has no still and no thumb, because there are no poster frames.
        if (kind == "still" && (!still || !thumb || video))
            throw response.Failure($"{clause}: for a still, links.still and links.thumb are set and links.video " +
                                   $"is null (SERVER_SPEC.md § 9.3). Got still={still}, thumb={thumb}, video={video}.");
        if (kind == "video" && (still || thumb || !video))
            throw response.Failure($"{clause}: for a video, links.video is set and links.still and links.thumb are " +
                                   $"null — there are no poster frames (SERVER_SPEC.md § 9.3). " +
                                   $"Got still={still}, thumb={thumb}, video={video}.");

        var size = reference.GetProperty("sizeBytes");
        var version = reference.GetProperty("mediaVersion");
        if (size.ValueKind == JsonValueKind.Null != (version.ValueKind == JsonValueKind.Null))
            throw response.Failure($"{clause}: {where}.mediaVersion is null exactly when sizeBytes is null " +
                                   "(SERVER_SPEC.md § 9.3).");

        if (version.ValueKind == JsonValueKind.String)
        {
            var value = version.GetString()!;
            if (value.Length != 16 || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw response.Failure($"{clause}: {where}.mediaVersion must be 16 lowercase hex characters " +
                                       $"(SERVER_SPEC.md § 12.2), got '{value}'.");
        }
    }

    public static void RequireErrorBody(JsonElement error, string clause, Rm2Response response)
    {
        foreach (var required in new[] { "code", "message", "requestId" })
            if (!error.TryGetProperty(required, out _))
                throw response.Failure($"{clause}: the error envelope requires '{required}'.");

        foreach (var property in error.EnumerateObject())
            if (property.Name is not ("code" or "message" or "requestId" or "details" or "session"))
                throw response.Failure($"{clause}: unexpected field 'error.{property.Name}'. " +
                                       "The envelope is exactly code, message, requestId, details, session.");

        var code = error.GetProperty("code").GetString();
        if (code is null || !ErrorStatuses.All.ContainsKey(code))
            throw response.Failure($"{clause}: '{code}' is not a code in SERVER_SPEC.md § 5. " +
                                   "New codes may be added to the spec, but a client branches on this value.");

        var message = error.GetProperty("message").GetString() ?? "";
        foreach (var leak in new[] { "   at ", "System.", "Exception:", "Microsoft." })
            if (message.Contains(leak, StringComparison.Ordinal))
                throw response.Failure($"{clause}: error.message looks like it carries an exception or a stack " +
                                       $"trace ('{leak}'). SERVER_SPEC.md § 4 forbids that; the detail belongs in " +
                                       "the log, keyed by requestId.");
    }

    public static void RequireExactKeys(JsonElement value, string what, IReadOnlyCollection<string> expected,
                                        string clause, Rm2Response response)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw response.Failure($"{clause}: expected {what} to be an object, got {value.ValueKind}.");

        var actual = value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(k => !actual.Contains(k)).ToArray();
        var extra = actual.Where(k => !expected.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        if (missing.Length == 0 && extra.Length == 0) return;

        var complaint = $"{clause}: {what} does not match the contract.";
        if (missing.Length > 0)
            complaint += $"\n  missing: {string.Join(", ", missing)} — every field is required, including the " +
                         "nullable ones, so a client never has to probe for a key.";
        if (extra.Length > 0)
            complaint += $"\n  unexpected: {string.Join(", ", extra)} — openapi.yaml sets additionalProperties: false.";

        throw response.Failure(complaint);
    }
}
