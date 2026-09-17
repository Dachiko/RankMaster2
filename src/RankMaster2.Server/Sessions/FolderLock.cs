using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RankMaster2.Server.Sessions;

/// <summary>
/// SERVER_SPEC.md § 10.4. <c>&lt;folder&gt;/.rankmaster.lock</c>, opened
/// <c>OpenOrCreate | ReadWrite | FileShare.None</c> and held open for the life of the session.
/// The extension is in neither media list, so <c>JsonCatalog</c> never scans it, never ranks it and
/// it never appears as a media id. A stale file left by a killed process is harmless: the OS
/// released the handle, so the next open succeeds.
/// </summary>
internal sealed class FolderLock : IDisposable
{
    public const string FileName = ".rankmaster.lock";

    private readonly FileStream _stream;

    private FolderLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    /// <summary>
    /// Takes the lock and stamps the holder record into it. Returns null when another process holds
    /// it — and says nothing about who, because it cannot: the file is open
    /// <c>FileShare.None</c>, so no one else can read it either (SERVER_SPEC.md § 5.3.1, which
    /// spends a paragraph telling implementers not to try).
    /// </summary>
    public static FolderLock? TryAcquire(string folder, string serverVersion)
    {
        var path = System.IO.Path.Combine(folder, FileName);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        HideOnWindows(path);

        try
        {
            var record = new LockHolder(
                Environment.ProcessId,
                Environment.MachineName,
                Process.GetCurrentProcess().ProcessName,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                serverVersion);

            var json = JsonSerializer.SerializeToUtf8Bytes(record);
            stream.SetLength(0);
            stream.Write(json, 0, json.Length);
            stream.Flush(true);
        }
        catch (IOException)
        {
            // The lock is ours even if the stamp did not land; the handle is what excludes others.
        }

        return new FolderLock(path, stream);
    }

    /// <summary>Closes the handle and removes the file. Neither step may throw out of a close.</summary>
    public void Dispose()
    {
        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
            // Nothing useful to do; the handle dies with the process anyway.
        }

        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// K7: on Windows the lock file sits in the owner's pictures folder in plain sight. It is
    /// bookkeeping, not a photograph, so it is marked Hidden — the same treatment Explorer gives its
    /// own <c>desktop.ini</c>. Nothing depends on this: the attribute is cosmetic, the exclusion is
    /// the open handle, and a filesystem that refuses the attribute (a FAT-formatted USB stick, a
    /// network share) is no reason to refuse the folder.
    /// </summary>
    private static void HideOnWindows(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The lock file body and, verbatim, <c>details.holder</c> of a <c>423 folder_locked</c>
/// (SERVER_SPEC.md § 5.3). Any field may be null.
/// </summary>
internal sealed record LockHolder(
    [property: JsonPropertyName("pid")] int? Pid,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("process")] string? Process,
    [property: JsonPropertyName("startedAt")] string? StartedAt,
    [property: JsonPropertyName("serverVersion")] string? ServerVersion);
