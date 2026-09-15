using System.Text.Json;
using RankMaster2.Server.Tests.Harness;
using Xunit.Sdk;

namespace RankMaster2.Audit.Conformance.Audit;

/// <summary>
/// SERVER_SPEC.md § 5, transcribed. Code → status, and the `details` keys § 5 fixes for it.
/// A code with no row in § 5's `details` column carries none, so an empty array here means
/// "the contract fixes no keys", not "unchecked".
/// </summary>
public static class Contract
{
    public static readonly IReadOnlyDictionary<string, int> Status = new Dictionary<string, int>
    {
        // § 5.1
        ["unauthenticated"] = 401,
        ["invalid_token"] = 401,
        ["token_revoked"] = 401,
        ["invalid_pairing_code"] = 401,
        ["pairing_not_open"] = 403,
        ["too_many_requests"] = 429,
        // § 5.2
        ["invalid_request"] = 400,
        ["missing_field"] = 400,
        ["invalid_side"] = 400,
        ["invalid_media_id"] = 400,
        ["unsupported_width"] = 400,
        ["unsupported_format"] = 400,
        ["invalid_path"] = 400,
        ["unsupported_content_type"] = 415,
        ["payload_too_large"] = 413,
        ["not_found"] = 404,
        // § 5.3
        ["no_session"] = 404,
        ["session_already_open"] = 409,
        ["folder_not_found"] = 404,
        ["folder_not_a_directory"] = 400,
        ["folder_access_denied"] = 403,
        ["folder_not_rankable"] = 409,
        ["library_json_unreadable"] = 409,
        ["folder_locked"] = 423,
        ["session_busy"] = 503,
        ["server_shutting_down"] = 503,
        // § 5.4
        ["stale_pair_token"] = 409,
        ["no_current_pair"] = 409,
        ["nothing_to_undo"] = 409,
        ["undo_folder_changed"] = 409,
        ["move_failed"] = 500,
        ["save_failed"] = 500,
        // § 5.5
        ["unknown_media_id"] = 404,
        ["media_file_missing"] = 404,
        ["media_outside_session"] = 403,
        ["media_extension_not_allowed"] = 403,
        ["wrong_media_kind"] = 409,
        ["media_decode_failed"] = 422,
        ["range_not_satisfiable"] = 416,
        // § 5.6
        ["internal_error"] = 500
    };

    /// <summary>The keys § 5 fixes inside `details` for a code. Absent from this map = none fixed.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DetailKeys = new Dictionary<string, string[]>
    {
        ["invalid_pairing_code"] = new[] { "attemptsRemaining" },
        ["too_many_requests"] = new[] { "retryAfterSeconds" },
        ["invalid_request"] = new[] { "field" },
        ["missing_field"] = new[] { "field" },
        ["invalid_side"] = new[] { "field", "value" },
        ["invalid_media_id"] = new[] { "reason" },
        ["unsupported_width"] = new[] { "requested", "allowed" },
        ["unsupported_format"] = new[] { "allowed" },
        ["invalid_path"] = new[] { "field" },
        ["payload_too_large"] = new[] { "maxBytes" },
        ["session_already_open"] = new[] { "openFolder" },
        ["folder_not_found"] = new[] { "folder" },
        ["folder_not_a_directory"] = new[] { "folder" },
        ["folder_access_denied"] = new[] { "folder" },
        ["folder_not_rankable"] = new[] { "stills", "videos", "rankable" },
        ["library_json_unreadable"] = new[] { "file" },
        ["folder_locked"] = new[] { "holder" },
        ["session_busy"] = new[] { "retryAfterSeconds" },
        ["server_shutting_down"] = new[] { "retryAfterSeconds" },
        ["stale_pair_token"] = new[] { "suppliedToken", "currentToken" },
        ["undo_folder_changed"] = new[] { "moveFolder" },
        ["move_failed"] = new[] { "id", "stage" },
        ["save_failed"] = new[] { "recordsChanged", "fileMoved" },
        ["unknown_media_id"] = new[] { "id" },
        ["media_file_missing"] = new[] { "id" },
        ["media_outside_session"] = new[] { "id" },
        ["media_extension_not_allowed"] = new[] { "id", "extension" },
        ["wrong_media_kind"] = new[] { "id", "kind", "endpoint" },
        ["media_decode_failed"] = new[] { "id" },
        ["range_not_satisfiable"] = new[] { "sizeBytes" }
    };

    /// <summary>§ 12.3.</summary>
    public static readonly int[] StillWidths = { 360, 540, 720, 1080, 1440, 2160 };

    public const int DefaultWidth = 1080;
    public const int ThumbWidth = 320;
    public const int MaxJsonBodyBytes = 65536;

    /// <summary>openapi.yaml, `SessionSnapshot.required` — every one, including the nullable ones.</summary>
    public static readonly string[] SnapshotKeys =
    {
        "sessionId", "state", "folder", "folderName", "policy", "openedAt", "prefetchPairs",
        "sessionVotes", "counts", "progress", "progressPercent", "cues", "pair", "pairToken",
        "pairSeq", "warmPairs", "undoAvailable", "lastAction", "lastSavedAt"
    };

    public static readonly string[] CountsKeys = { "total", "rankable", "unranked", "stills", "videos" };
    public static readonly string[] MediaRefKeys = { "id", "kind", "sizeBytes", "mediaVersion", "links" };
    public static readonly string[] LinkKeys = { "meta", "still", "thumb", "video" };

    public static readonly string[] LastActionKeys =
    {
        "seq", "type", "pairToken", "clientRequestId", "winner", "side", "id", "restoredId", "at"
    };

    public static readonly string[] MetaKeys =
    {
        "id", "kind", "sizeBytes", "modifiedAt", "mediaVersion", "width", "height", "rating",
        "matches", "impressions", "lastPlayed", "rankable"
    };

    public static readonly string[] RatingKeys = { "mu", "sigma", "conservative" };

    /// <summary>§ 12.1: MediaMeta MUST NOT carry anything that needs a video decoded.</summary>
    public static readonly string[] ForbiddenMetaKeys = { "durationMs", "codec", "frameRate", "bitrate" };

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

    public static readonly string[] BrowseEntryKeys =
    {
        "name", "path", "stillCount", "videoCount", "rankable", "hasDatabase", "accessible"
    };

    public static readonly string[] RootEntryKeys =
    {
        "path", "label", "kind", "available", "totalBytes", "freeBytes"
    };

    public static readonly string[] ActionTypes = { "vote", "skip", "discard", "special", "undo", "drop_missing" };
}

/// <summary>
/// The audit's own assertions. Deliberately not the other suite's: every message names the clause
/// it is enforcing so a failure is a finding, readable without the test source in hand.
/// </summary>
public static class Check
{
    public static JsonElement RequireJson(this Rm2Response r, string clause) =>
        r.Json ?? throw r.Failure($"{clause}: expected a JSON body and the body did not parse as JSON.");

    public static Rm2Response Status(this Rm2Response r, int expected, string clause)
    {
        if (r.StatusCode != expected)
            throw r.Failure($"{clause}: expected HTTP {expected}, got {r.StatusCode}.");
        return r;
    }

    public static Rm2Response StatusOneOf(this Rm2Response r, string clause, params int[] expected)
    {
        if (!expected.Contains(r.StatusCode))
            throw r.Failure($"{clause}: expected one of HTTP [{string.Join(", ", expected)}], got {r.StatusCode}.");
        return r;
    }

    /// <summary>
    /// The one error envelope of § 4: `error` is an object with `code`, `message` and `requestId`,
    /// `requestId` equals the header, and § 5 binds the code to this exact status.
    /// </summary>
    public static JsonElement Error(this Rm2Response r, string expectedCode, string clause)
    {
        var body = r.RequireJson($"{clause}: SERVER_SPEC.md § 4 gives every non-2xx a JSON envelope");

        if (!body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            throw r.Failure($"{clause}: SERVER_SPEC.md § 4 — the body must be {{\"error\": {{…}}}} and nothing else.");

        foreach (var key in new[] { "code", "message", "requestId" })
            if (!error.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                throw r.Failure($"{clause}: SERVER_SPEC.md § 4 requires a string error.{key}.");

        var code = error.GetProperty("code").GetString();
        if (code != expectedCode)
            throw r.Failure($"{clause}: expected error.code '{expectedCode}', got '{code}'.");

        if (!Contract.Status.TryGetValue(expectedCode, out var bound))
            throw r.Failure($"{clause}: '{expectedCode}' is not a code in SERVER_SPEC.md § 5.");

        if (r.StatusCode != bound)
            throw r.Failure($"{clause}: SERVER_SPEC.md § 5 binds '{expectedCode}' to HTTP {bound}; " +
                            $"this response was {r.StatusCode}. A code's status is part of the contract.");

        var requestId = error.GetProperty("requestId").GetString();
        if (requestId != r.RequestId)
            throw r.Failure($"{clause}: SERVER_SPEC.md § 4 — error.requestId ('{requestId}') must equal " +
                            $"the X-Request-Id header ('{r.RequestId}').");

        return error;
    }

    /// <summary>Any envelope, whatever the code — for tests that assert on the shape, not the code.</summary>
    public static JsonElement AnyError(this Rm2Response r, string clause)
    {
        var body = r.RequireJson($"{clause}: SERVER_SPEC.md § 4 gives every non-2xx a JSON envelope");
        if (!body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            throw r.Failure($"{clause}: SERVER_SPEC.md § 4 — the body must be {{\"error\": {{…}}}}.");
        return error;
    }

    public static string ErrorCodeOrThrow(this Rm2Response r, string clause) =>
        r.AnyError(clause).GetProperty("code").GetString()!;

    /// <summary>The `details` object § 5 fixes for this code, with every key it names present.</summary>
    public static JsonElement Details(this Rm2Response r, JsonElement error, string clause)
    {
        var code = error.GetProperty("code").GetString()!;
        if (!Contract.DetailKeys.TryGetValue(code, out var keys))
            throw new XunitException($"{clause}: SERVER_SPEC.md § 5 fixes no details for '{code}'.");

        if (!error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
            throw r.Failure($"{clause}: SERVER_SPEC.md § 5 gives '{code}' a details object of " +
                            $"{{{string.Join(", ", keys)}}}, and this response carries none.");

        var missing = keys.Where(k => !details.TryGetProperty(k, out _)).ToArray();
        if (missing.Length > 0)
            throw r.Failure($"{clause}: error.details for '{code}' is missing {string.Join(", ", missing)} " +
                            $"(SERVER_SPEC.md § 5).");

        return details;
    }

    public static void Header(this Rm2Response r, string name, string expected, string clause)
    {
        var actual = r.HeaderOrNull(name);
        if (actual is null)
            throw r.Failure($"{clause}: the response carries no {name} header.");
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw r.Failure($"{clause}: {name} is '{actual}', expected '{expected}'.");
    }

    public static void NoHeader(this Rm2Response r, string name, string clause)
    {
        var actual = r.HeaderOrNull(name);
        if (actual is not null)
            throw r.Failure($"{clause}: the response must not carry {name}, but it is '{actual}'.");
    }

    // ---- object shapes -------------------------------------------------------------------------

    public static void RequireKeys(this Rm2Response r, JsonElement obj, IEnumerable<string> keys,
                                   string what, string clause)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw r.Failure($"{clause}: expected {what} to be a JSON object, got {obj.ValueKind}.");

        var missing = keys.Where(k => !obj.TryGetProperty(k, out _)).ToArray();
        if (missing.Length > 0)
            throw r.Failure($"{clause}: {what} is missing required {(missing.Length == 1 ? "field" : "fields")} " +
                            $"{string.Join(", ", missing)}. openapi.yaml lists every one as required — including the " +
                            "nullable ones, so a client can read the key without first testing for it. A 200 with a " +
                            "missing field is a violation.");
    }

    public static void RequireNoExtraKeys(this Rm2Response r, JsonElement obj, IEnumerable<string> allowed,
                                          string what, string clause)
    {
        var extra = obj.EnumerateObject().Select(p => p.Name).Except(allowed).ToArray();
        if (extra.Length > 0)
            throw r.Failure($"{clause}: {what} carries {string.Join(", ", extra)}, which openapi.yaml forbids " +
                            $"(additionalProperties: false).");
    }

    public static JsonElement Field(this Rm2Response r, JsonElement obj, string name, JsonValueKind kind,
                                    string what, string clause)
    {
        if (!obj.TryGetProperty(name, out var value))
            throw r.Failure($"{clause}: {what}.{name} is absent.");
        if (value.ValueKind != kind)
            throw r.Failure($"{clause}: {what}.{name} must be {kind}, got {value.ValueKind} ({value}).");
        return value;
    }

    public static void Nullable(this Rm2Response r, JsonElement obj, string name, JsonValueKind kind,
                                string what, string clause)
    {
        if (!obj.TryGetProperty(name, out var value))
            throw r.Failure($"{clause}: {what}.{name} is absent; openapi.yaml requires the key even when null.");
        if (value.ValueKind != kind && value.ValueKind != JsonValueKind.Null)
            throw r.Failure($"{clause}: {what}.{name} must be {kind} or null, got {value.ValueKind}.");
    }

    public static void Integer(this Rm2Response r, JsonElement obj, string name, string what, string clause)
    {
        var value = r.Field(obj, name, JsonValueKind.Number, what, clause);
        if (!value.TryGetInt64(out _))
            throw r.Failure($"{clause}: {what}.{name} must be an integer, got {value}.");
    }

    public static void Bool(this Rm2Response r, JsonElement obj, string name, string what, string clause)
    {
        if (!obj.TryGetProperty(name, out var value))
            throw r.Failure($"{clause}: {what}.{name} is absent.");
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw r.Failure($"{clause}: {what}.{name} must be a boolean, got {value.ValueKind}.");
    }

    /// <summary>§ 2: RFC 3339 UTC with a literal `Z`.</summary>
    public static void Rfc3339Utc(this Rm2Response r, string? value, string what, string clause)
    {
        if (value is null)
            throw r.Failure($"{clause}: {what} is null where a timestamp is required.");
        if (!value.EndsWith("Z", StringComparison.Ordinal))
            throw r.Failure($"{clause}: SERVER_SPEC.md § 2 requires RFC 3339 UTC with a trailing 'Z'; " +
                            $"{what} is '{value}'.");
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                                     System.Globalization.DateTimeStyles.RoundtripKind, out _))
            throw r.Failure($"{clause}: {what} ('{value}') is not a parseable RFC 3339 timestamp.");
    }

    /// <summary>
    /// The full § 9.1 snapshot: every key present, every declared type honoured, and the invariants
    /// § 9.1–9.5 state between them.
    /// </summary>
    public static Snapshot RequireSnapshot(this Rm2Response r, int expectedStatus, string clause)
    {
        r.Status(expectedStatus, clause);
        var s = r.RequireJson($"{clause}: SERVER_SPEC.md § 9 makes every 2xx from /session* a SessionSnapshot");

        r.RequireKeys(s, Contract.SnapshotKeys, "SessionSnapshot", clause);
        r.RequireNoExtraKeys(s, Contract.SnapshotKeys, "SessionSnapshot", clause);

        r.Field(s, "sessionId", JsonValueKind.String, "SessionSnapshot", clause);
        var state = r.Field(s, "state", JsonValueKind.String, "SessionSnapshot", clause).GetString();
        if (state is not ("ranking" or "exhausted"))
            throw r.Failure($"{clause}: state must be 'ranking' or 'exhausted' (§ 7.1), got '{state}'.");

        r.Field(s, "folder", JsonValueKind.String, "SessionSnapshot", clause);
        r.Field(s, "folderName", JsonValueKind.String, "SessionSnapshot", clause);

        var policy = r.Field(s, "policy", JsonValueKind.String, "SessionSnapshot", clause).GetString();
        if (policy is not ("still" or "video"))
            throw r.Failure($"{clause}: policy must be 'still' or 'video' (§ 9.1), got '{policy}'.");

        r.Rfc3339Utc(s.GetProperty("openedAt").GetString(), "openedAt", clause);
        r.Integer(s, "prefetchPairs", "SessionSnapshot", clause);
        r.Integer(s, "sessionVotes", "SessionSnapshot", clause);
        r.Integer(s, "pairSeq", "SessionSnapshot", clause);
        r.Integer(s, "progressPercent", "SessionSnapshot", clause);
        r.Field(s, "progress", JsonValueKind.Number, "SessionSnapshot", clause);
        r.Bool(s, "undoAvailable", "SessionSnapshot", clause);
        r.Nullable(s, "lastSavedAt", JsonValueKind.String, "SessionSnapshot", clause);
        if (s.GetProperty("lastSavedAt").ValueKind == JsonValueKind.String)
            r.Rfc3339Utc(s.GetProperty("lastSavedAt").GetString(), "lastSavedAt", clause);

        // counts
        var counts = r.Field(s, "counts", JsonValueKind.Object, "SessionSnapshot", clause);
        r.RequireKeys(counts, Contract.CountsKeys, "Counts", clause);
        r.RequireNoExtraKeys(counts, Contract.CountsKeys, "Counts", clause);
        foreach (var key in Contract.CountsKeys) r.Integer(counts, key, "Counts", clause);

        var stills = counts.GetProperty("stills").GetInt32();
        var videos = counts.GetProperty("videos").GetInt32();
        var total = counts.GetProperty("total").GetInt32();
        var rankable = counts.GetProperty("rankable").GetInt32();
        if (stills + videos != total)
            throw r.Failure($"{clause}: § 9.2 requires stills + videos == total; got {stills} + {videos} != {total}.");
        var expectedRankable = policy == "still" ? stills : videos;
        if (rankable != expectedRankable)
            throw r.Failure($"{clause}: § 9.2 requires rankable == {(policy == "still" ? "stills" : "videos")} " +
                            $"for policy '{policy}'; got rankable={rankable}, {(policy == "still" ? "stills" : "videos")}={expectedRankable}.");

        // cues
        var cues = r.Field(s, "cues", JsonValueKind.Array, "SessionSnapshot", clause);
        if (cues.GetArrayLength() > 10)
            throw r.Failure($"{clause}: § 9.1 caps cues at 10 (MatchCueLimit); got {cues.GetArrayLength()}.");
        foreach (var cue in cues.EnumerateArray())
            if (cue.GetString() is not ("confirmation" or "upset"))
                throw r.Failure($"{clause}: cues may only hold 'confirmation' or 'upset'; found '{cue}'.");

        // pair / pairToken
        var pair = s.GetProperty("pair");
        var token = s.GetProperty("pairToken");
        if (state == "exhausted")
        {
            if (pair.ValueKind != JsonValueKind.Null)
                throw r.Failure($"{clause}: § 9.1 — pair MUST be null when state is 'exhausted'.");
            if (token.ValueKind != JsonValueKind.Null)
                throw r.Failure($"{clause}: § 8.2 — pairToken MUST be null when state is 'exhausted'.");
        }
        else
        {
            if (pair.ValueKind != JsonValueKind.Object)
                throw r.Failure($"{clause}: § 9.1 — pair MUST be a Pair when state is 'ranking'.");
            if (token.ValueKind != JsonValueKind.String)
                throw r.Failure($"{clause}: § 9.1 — pairToken is null iff pair is null; here pair is present " +
                                $"and pairToken is {token.ValueKind}.");
            r.RequirePair(pair, clause);
        }

        // warm pairs
        var warm = r.Field(s, "warmPairs", JsonValueKind.Array, "SessionSnapshot", clause);
        var prefetch = s.GetProperty("prefetchPairs").GetInt32();
        if (warm.GetArrayLength() > prefetch)
            throw r.Failure($"{clause}: § 9.1 — warmPairs never exceeds prefetchPairs ({prefetch}); " +
                            $"got {warm.GetArrayLength()}.");
        foreach (var w in warm.EnumerateArray()) r.RequirePair(w, clause);

        // lastAction
        var last = s.GetProperty("lastAction");
        if (last.ValueKind == JsonValueKind.Object)
        {
            r.RequireKeys(last, Contract.LastActionKeys, "LastAction", clause);
            r.RequireNoExtraKeys(last, Contract.LastActionKeys, "LastAction", clause);
            r.Integer(last, "seq", "LastAction", clause);
            var type = r.Field(last, "type", JsonValueKind.String, "LastAction", clause).GetString();
            if (!Contract.ActionTypes.Contains(type))
                throw r.Failure($"{clause}: § 9.4 — lastAction.type must be one of " +
                                $"[{string.Join(", ", Contract.ActionTypes)}], got '{type}'.");
            foreach (var key in new[] { "pairToken", "clientRequestId", "winner", "side", "id", "restoredId" })
                r.Nullable(last, key, JsonValueKind.String, "LastAction", clause);
            r.Rfc3339Utc(last.GetProperty("at").GetString(), "lastAction.at", clause);
        }
        else if (last.ValueKind != JsonValueKind.Null)
        {
            throw r.Failure($"{clause}: lastAction must be an object or null, got {last.ValueKind}.");
        }

        return new Snapshot(s, r);
    }

    public static void RequirePair(this Rm2Response r, JsonElement pair, string clause)
    {
        r.RequireKeys(pair, new[] { "left", "right" }, "Pair", clause);
        r.RequireNoExtraKeys(pair, new[] { "left", "right" }, "Pair", clause);

        var left = pair.GetProperty("left");
        var right = pair.GetProperty("right");
        r.RequireMediaRef(left, clause);
        r.RequireMediaRef(right, clause);

        if (left.GetProperty("id").GetString() == right.GetProperty("id").GetString())
            throw r.Failure($"{clause}: § 9.3 — left and right are never equal; both are " +
                            $"'{left.GetProperty("id").GetString()}'.");
    }

    public static void RequireMediaRef(this Rm2Response r, JsonElement item, string clause)
    {
        r.RequireKeys(item, Contract.MediaRefKeys, "MediaRef", clause);
        r.RequireNoExtraKeys(item, Contract.MediaRefKeys, "MediaRef", clause);

        r.Field(item, "id", JsonValueKind.String, "MediaRef", clause);
        var kind = r.Field(item, "kind", JsonValueKind.String, "MediaRef", clause).GetString();
        if (kind is not ("still" or "video"))
            throw r.Failure($"{clause}: MediaRef.kind must be 'still' or 'video', got '{kind}'.");

        r.Nullable(item, "sizeBytes", JsonValueKind.Number, "MediaRef", clause);
        r.Nullable(item, "mediaVersion", JsonValueKind.String, "MediaRef", clause);

        var size = item.GetProperty("sizeBytes");
        var version = item.GetProperty("mediaVersion");
        if (size.ValueKind == JsonValueKind.Null && version.ValueKind != JsonValueKind.Null)
            throw r.Failure($"{clause}: § 9.3 — mediaVersion is null when sizeBytes is null; " +
                            $"here sizeBytes is null and mediaVersion is '{version}'.");
        if (version.ValueKind == JsonValueKind.String && !IsMediaVersion(version.GetString()!))
            throw r.Failure($"{clause}: § 12.2 — mediaVersion is 16 lowercase hex characters; " +
                            $"got '{version.GetString()}'.");

        var links = r.Field(item, "links", JsonValueKind.Object, "MediaRef", clause);
        r.RequireKeys(links, Contract.LinkKeys, "MediaLinks", clause);
        r.RequireNoExtraKeys(links, Contract.LinkKeys, "MediaLinks", clause);
        r.Field(links, "meta", JsonValueKind.String, "MediaLinks", clause);

        if (kind == "still")
        {
            if (links.GetProperty("video").ValueKind != JsonValueKind.Null)
                throw r.Failure($"{clause}: § 9.3 — links.video is null for a still.");
            foreach (var name in new[] { "still", "thumb" })
                if (links.GetProperty(name).ValueKind != JsonValueKind.String)
                    throw r.Failure($"{clause}: § 9.3 — links.{name} must be a URL for a still, " +
                                    $"got {links.GetProperty(name).ValueKind}.");
        }
        else
        {
            foreach (var name in new[] { "still", "thumb" })
                if (links.GetProperty(name).ValueKind != JsonValueKind.Null)
                    throw r.Failure($"{clause}: § 9.3 — links.{name} is null for a video " +
                                    "(there are no poster frames).");
            if (links.GetProperty("video").ValueKind != JsonValueKind.String)
                throw r.Failure($"{clause}: § 9.3 — links.video must be a URL for a video.");
        }
    }

    public static bool IsMediaVersion(string value) =>
        value.Length == 16 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsBase64Url(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
