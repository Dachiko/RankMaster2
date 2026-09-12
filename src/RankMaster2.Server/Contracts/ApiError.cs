namespace RankMaster2.Server.Contracts;

/// <summary>
/// The single error shape. SERVER_SPEC.md § 4: every non-2xx response with a body uses exactly
/// this, and clients branch on <see cref="ApiError.Code"/> — never on the message.
/// </summary>
public sealed record ApiErrorEnvelope(ApiError Error);

public sealed record ApiError(
    string Code,
    string Message,
    string RequestId,
    object? Details = null,
    object? Session = null);

/// <summary>
/// The codes from SERVER_SPEC.md § 5 and the status each one carries. Generated from the spec
/// tables so the four implementations cannot drift apart. A code's status is part of the
/// contract: existing codes MUST NOT change meaning or status.
/// </summary>
public static class ErrorCodes
{
    public const string Unauthenticated = "unauthenticated";
    public const string InvalidToken = "invalid_token";
    public const string TokenRevoked = "token_revoked";
    public const string InvalidPairingCode = "invalid_pairing_code";
    public const string PairingNotOpen = "pairing_not_open";
    public const string TooManyRequests = "too_many_requests";
    public const string InvalidRequest = "invalid_request";
    public const string MissingField = "missing_field";
    public const string InvalidSide = "invalid_side";
    public const string InvalidMediaId = "invalid_media_id";
    public const string UnsupportedWidth = "unsupported_width";
    public const string UnsupportedFormat = "unsupported_format";
    public const string InvalidPath = "invalid_path";
    public const string UnsupportedContentType = "unsupported_content_type";
    public const string PayloadTooLarge = "payload_too_large";
    public const string NotFound = "not_found";
    public const string NoSession = "no_session";
    public const string SessionAlreadyOpen = "session_already_open";
    public const string FolderNotFound = "folder_not_found";
    public const string FolderNotADirectory = "folder_not_a_directory";
    public const string FolderAccessDenied = "folder_access_denied";
    public const string FolderNotRankable = "folder_not_rankable";
    public const string LibraryJsonUnreadable = "library_json_unreadable";
    public const string FolderLocked = "folder_locked";
    public const string SessionBusy = "session_busy";
    public const string ServerShuttingDown = "server_shutting_down";
    public const string StalePairToken = "stale_pair_token";
    public const string NoCurrentPair = "no_current_pair";
    public const string NothingToUndo = "nothing_to_undo";
    public const string UndoFolderChanged = "undo_folder_changed";
    public const string MoveFailed = "move_failed";
    public const string SaveFailed = "save_failed";
    public const string UnknownMediaId = "unknown_media_id";
    public const string MediaFileMissing = "media_file_missing";
    public const string MediaOutsideSession = "media_outside_session";
    public const string MediaExtensionNotAllowed = "media_extension_not_allowed";
    public const string WrongMediaKind = "wrong_media_kind";
    public const string MediaDecodeFailed = "media_decode_failed";
    public const string RangeNotSatisfiable = "range_not_satisfiable";
    public const string InternalError = "internal_error";

    private static readonly Dictionary<string, int> Statuses = new()
    {
        ["unauthenticated"] = 401,
        ["invalid_token"] = 401,
        ["token_revoked"] = 401,
        ["invalid_pairing_code"] = 401,
        ["pairing_not_open"] = 403,
        ["too_many_requests"] = 429,
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
        ["stale_pair_token"] = 409,
        ["no_current_pair"] = 409,
        ["nothing_to_undo"] = 409,
        ["undo_folder_changed"] = 409,
        ["move_failed"] = 500,
        ["save_failed"] = 500,
        ["unknown_media_id"] = 404,
        ["media_file_missing"] = 404,
        ["media_outside_session"] = 403,
        ["media_extension_not_allowed"] = 403,
        ["wrong_media_kind"] = 409,
        ["media_decode_failed"] = 422,
        ["range_not_satisfiable"] = 416,
        ["internal_error"] = 500,
    };

    /// <summary>The status this code is contractually bound to. Unknown codes are a bug, not a 500.</summary>
    public static int StatusOf(string code) =>
        Statuses.TryGetValue(code, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Not a code in SERVER_SPEC.md § 5.");

    public static bool IsKnown(string code) => Statuses.ContainsKey(code);

    public static IReadOnlyDictionary<string, int> All => Statuses;
}
