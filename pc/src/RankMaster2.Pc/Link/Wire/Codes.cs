namespace RankMaster2.Pc.Link.Wire;

/// <summary>
/// The SERVER_SPEC.md § 5 error codes the link branches on, plus the synthetic <c>client_*</c> codes
/// it invents for answers that are not one of the server's. The synthetic codes carry the
/// <c>client_</c> prefix so they can never collide with a § 5 code added later
/// (<c>Rm2SyntheticCodes</c> in the phone's Kotlin client).
/// </summary>
internal static class Codes
{
    // § 5.1 authentication and pairing
    public const string Unauthenticated = "unauthenticated";
    public const string InvalidToken = "invalid_token";
    public const string TokenRevoked = "token_revoked";
    public const string InvalidPairingCode = "invalid_pairing_code";
    public const string PairingNotOpen = "pairing_not_open";
    public const string TooManyRequests = "too_many_requests";

    // § 5.3 session and folder
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

    // § 5.4 pair and actions
    public const string StalePairToken = "stale_pair_token";
    public const string NoCurrentPair = "no_current_pair";
    public const string NothingToUndo = "nothing_to_undo";
    public const string UndoFolderChanged = "undo_folder_changed";
    public const string MoveFailed = "move_failed";
    public const string SaveFailed = "save_failed";

    // SERVER_SPEC.md § 10.16 rename by rank
    public const string RenameInProgress = "rename_in_progress";
    public const string RenameFailed = "rename_failed";
    public const string NoRenameOperation = "no_rename_operation";

    // § 5.6 server
    public const string InternalError = "internal_error";

    // ---- synthetic, this client's own -----------------------------------------------------------

    /// <summary>A non-2xx that did not carry a readable § 4 envelope, or carried no body at all.</summary>
    public const string ClientMalformedError = "client_malformed_error";

    /// <summary>A 2xx whose body is not the shape this client expects.</summary>
    public const string ClientMalformedResponse = "client_malformed_response";

    /// <summary>No definite answer after the retry (§ 5.1.4): a timeout on both attempts.</summary>
    public const string ClientTimeout = "client_timeout";

    /// <summary>The caller's <see cref="System.Threading.CancellationToken"/> fired before an answer came back.</summary>
    public const string ClientCancelled = "client_cancelled";

    /// <summary>The peer's certificate did not match the pin (§ 5.1.3).</summary>
    public const string ClientPinMismatch = "client_pin_mismatch";

    /// <summary>Every other transport failure: DNS, connection refused, reset, etc.</summary>
    public const string ClientUnreachable = "client_unreachable";
}
