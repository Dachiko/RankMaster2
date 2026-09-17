namespace RankMaster2.Server.Security;

/// <summary>
/// The out-of-band channel that revokes a device other than the caller's own.
///
/// <para><b>Why this exists.</b> <c>DELETE /pair/{deviceId}</c> (§ 10.12) is authenticated by a
/// bearer token like every other route, and a bearer token proves only "some paired device", not
/// "the owner". Letting any paired device revoke any other over that same HTTP call would mean a
/// lent or forgotten device — or a stolen one — could log out every other device on the account
/// with nothing more than its own still-valid token (AUDIT2.md § 3.13). A device revoking
/// <b>itself</b> is fine over HTTP: that is "Forget this PC", and it proves nothing beyond what the
/// device already had the authority to do to its own session. Revoking a <b>different</b> device is
/// a decision only the owner gets to make, so it needs the same proof opening a pairing window
/// needs (§ 10.1.1): control of the owner's OS account, demonstrated by writing a file into a
/// directory only that account can write.</para>
///
/// <para><b>The file.</b> <c>&lt;data&gt;/revoke.request</c>, one line of UTF-8 text: the
/// <c>deviceId</c> to revoke. The server polls for it the same way and on the same cadence it polls
/// for <c>pair.request</c>, and applies the same staleness rule (<see
/// cref="PairingService.MaxPairRequestAge"/>) for the same reason: a request that sat unclaimed
/// long enough to be stale was not written by someone still watching for the result, so it is
/// consumed and dropped rather than fired late and unattended.</para>
/// </summary>
internal static class RevocationChannel
{
    private const string RequestFileName = "revoke.request";
    private const int MaxDeviceIdLength = 64;

    public static string RequestFilePath(string dataDirectory) => Path.Combine(dataDirectory, RequestFileName);

    /// <summary>
    /// Polls for a revoke request. Returns the <c>deviceId</c> to revoke when a fresh one was
    /// found; <c>null</c> when there was none, or when what was there was stale or unusable — in
    /// every one of those cases the file, if present, has already been deleted by the time this
    /// returns.
    /// </summary>
    public static string? Consume(string dataDirectory, DateTimeOffset now)
    {
        var path = RequestFilePath(dataDirectory);

        try
        {
            if (!File.Exists(path)) return null;

            var age = now - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var deviceId = File.ReadAllText(path).Trim();
            File.Delete(path);

            if (age > PairingService.MaxPairRequestAge) return null;
            if (string.IsNullOrEmpty(deviceId) || deviceId.Length > MaxDeviceIdLength) return null;

            return deviceId;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
