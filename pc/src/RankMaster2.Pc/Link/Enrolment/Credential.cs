using System.Text.Json;
using System.Text.Json.Serialization;

namespace RankMaster2.Pc.Link.Enrolment;

/// <summary>
/// What enrolling with the server produced, held in <c>link.json</c> next to the credential
/// directory: the address, the pinned fingerprint, the bearer token, and enough to revoke the old
/// device on a re-enrolment (§ 5.2.3 step 4).
/// </summary>
internal sealed record Credential(
    string BaseUrl,
    string Fingerprint,
    string Token,
    string DeviceId,
    string PairedAt)
{
    private const string FileName = "link.json";

    public static Credential? Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(bytes, CredentialJsonContext.Default.Credential);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A half-written or corrupt credential file is treated as "no credential": ConnectAsync
            // falls through to enrolment, which overwrites it with a good one.
            return null;
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var tmp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, CredentialJsonContext.Default.Credential);

        File.WriteAllBytes(tmp, bytes);
        TrySetOwnerOnly(tmp);
        File.Move(tmp, path, overwrite: true);
        TrySetOwnerOnly(path);
    }

    private static void TrySetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return; // relies on the profile directory's ACL (§ 8, gate item 9)
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { }
        catch (PlatformNotSupportedException) { }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(Credential))]
internal sealed partial class CredentialJsonContext : JsonSerializerContext
{
}
