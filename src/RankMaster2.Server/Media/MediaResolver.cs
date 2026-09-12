namespace RankMaster2.Server.Media;

/// <summary>A resolved, verified media file: the record, the canonical path and its stat.</summary>
public sealed record ResolvedMedia(
    MediaRecord Record,
    IMediaSessionView Session,
    string Path,
    long SizeBytes,
    DateTime ModifiedUtc)
{
    public string Id => Record.Id.Filename;

    public MediaKind Kind => Record.Kind;

    public MediaFingerprint Fingerprint => MediaFingerprint.Of(Id, SizeBytes, ModifiedUtc.Ticks);

    public string MediaVersion => Fingerprint.MediaVersion;
}

/// <summary>What went wrong, in the order § 11.2 requires it to be discovered.</summary>
public enum MediaResolution
{
    Ok,
    NoSession,
    InvalidId,
    OutsideSession,
    ExtensionNotAllowed,
    UnknownId,
    FileMissing,
}

public readonly record struct MediaResolveResult(
    MediaResolution Resolution,
    ResolvedMedia? Media,
    string RequestedId,
    string? Reason)
{
    public bool Ok => Resolution == MediaResolution.Ok;
}

/// <summary>
/// SERVER_SPEC.md § 11.2, step for step and in order. This is the one place in the API where a
/// bearer token does not buy access to the whole filesystem, so the order is not an
/// implementation detail — each step's error code tells the client something different, and
/// § 6 spells out what: "A 404 says 'not part of this session'; a 403 says 'you tried to leave
/// the session folder'."
/// <para/>
/// Step 5 is the security backstop and it runs <b>after</b> resolution, never before: combine,
/// canonicalise with <c>Path.GetFullPath</c>, and only then check that the result's directory is
/// exactly the session folder. A check performed on the unresolved string is a check that a
/// separator, a <c>..</c> or a symlink walks straight past.
/// </summary>
public sealed class MediaResolver(IMediaSessionAccessor sessions)
{
    public MediaResolveResult Resolve(string rawIdSegment)
    {
        // 1. No session open → 404 no_session. There is no way to fetch bytes without one.
        var session = sessions.Current;
        if (session is null)
            return new MediaResolveResult(MediaResolution.NoSession, null, "", null);

        // 2. Id fails § 11.1 → 400 invalid_media_id or 403 media_outside_session.
        var decoded = MediaIdCodec.Decode(rawIdSegment);
        if (!decoded.Ok)
        {
            return new MediaResolveResult(
                decoded.Fault == MediaIdFault.Escapes ? MediaResolution.OutsideSession : MediaResolution.InvalidId,
                null,
                rawIdSegment,
                decoded.Reason);
        }

        var id = decoded.Id;

        // 3. Extension in neither list of SPEC.md § Media policy → 403 media_extension_not_allowed.
        //    Redundant with step 4 in practice, and checked anyway so the rule holds even if the
        //    record set is ever populated from somewhere other than JsonCatalog.
        if (MediaExtensions.KindOf(id) is null)
        {
            return new MediaResolveResult(
                MediaResolution.ExtensionNotAllowed,
                null,
                id,
                Path.GetExtension(id).TrimStart('.').ToLowerInvariant());
        }

        // 4. Not a record in the open session → 404 unknown_media_id. Membership is against
        //    RankingSession.Records: videos in a mixed folder are in, discarded files are out.
        if (!session.TryFindRecord(id, out var record))
            return new MediaResolveResult(MediaResolution.UnknownId, null, id, null);

        // From here the on-disk spelling is the id, not the caller's (§ 11.1.6).
        var canonicalId = record.Id.Filename;

        // 5. Combine, resolve, and verify the result's directory is exactly the session folder.
        string fullPath;
        string folder;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.Folder));
            fullPath = Path.GetFullPath(Path.Combine(folder, canonicalId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new MediaResolveResult(MediaResolution.OutsideSession, null, canonicalId, "unresolvable");
        }

        var parent = Path.GetDirectoryName(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent is null || !Path.TrimEndingDirectorySeparator(parent).Equals(folder, comparison))
            return new MediaResolveResult(MediaResolution.OutsideSession, null, canonicalId, "outside-folder");

        // The leaf must still be the id after canonicalisation. Subfolders are never reachable:
        // discarded/, special 1/ and rankmaster_backup_* hold files that are no longer records.
        if (!Path.GetFileName(fullPath).Equals(canonicalId, comparison))
            return new MediaResolveResult(MediaResolution.OutsideSession, null, canonicalId, "renamed-by-resolution");

        // 6. The file does not exist → 404 media_file_missing.
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return new MediaResolveResult(MediaResolution.FileMissing, null, canonicalId, null);

        var resolved = new ResolvedMedia(
            record,
            session,
            fullPath,
            info.Length,
            File.GetLastWriteTimeUtc(fullPath));

        return new MediaResolveResult(MediaResolution.Ok, resolved, canonicalId, null);
    }
}
