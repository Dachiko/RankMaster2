namespace RankMaster2.Server.Tests.Harness;

/// <summary>
/// The code-to-status table of SERVER_SPEC.md § 5, transcribed here independently of the server's
/// own copy in <c>Contracts/ApiError.cs</c>.
///
/// Two transcriptions of one table is not duplication for its own sake: it is the only way a test
/// can catch a typo in the table the server answers from. <see cref="ErrorCodeTableTests"/> compares
/// the two, so a drift in either direction is a failure rather than a silently shared mistake.
/// </summary>
public static class ErrorStatuses
{
    public static readonly IReadOnlyDictionary<string, int> All = new Dictionary<string, int>
    {
        // § 5.1 Authentication and pairing
        ["unauthenticated"] = 401,
        ["invalid_token"] = 401,
        ["token_revoked"] = 401,
        ["invalid_pairing_code"] = 401,
        ["pairing_not_open"] = 403,
        ["too_many_requests"] = 429,

        // § 5.2 Request shape
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

        // § 5.3 Session and folder
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

        // § 5.4 Pair and actions
        ["stale_pair_token"] = 409,
        ["no_current_pair"] = 409,
        ["nothing_to_undo"] = 409,
        ["undo_folder_changed"] = 409,
        ["move_failed"] = 500,
        ["save_failed"] = 500,

        // § 5.5 Media
        ["unknown_media_id"] = 404,
        ["media_file_missing"] = 404,
        ["media_outside_session"] = 403,
        ["media_extension_not_allowed"] = 403,
        ["wrong_media_kind"] = 409,
        ["media_decode_failed"] = 422,
        ["range_not_satisfiable"] = 416,

        // § 5.6 Server
        ["internal_error"] = 500,

        // § 5.7 Rename (§ 10.16)
        ["rename_in_progress"] = 409,
        ["no_rename_operation"] = 404,
        ["rename_failed"] = 500,
    };

    public static int Of(string code) =>
        All.TryGetValue(code, out var status)
            ? status
            : throw new ArgumentOutOfRangeException(nameof(code), code,
                "Not a code in SERVER_SPEC.md § 5. The test is asserting a code the contract does not define.");
}
