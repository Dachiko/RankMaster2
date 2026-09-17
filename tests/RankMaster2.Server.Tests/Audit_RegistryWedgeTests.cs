using System.Text.Json;
using RankMaster2.Catalog;
using RankMaster2.Server.Contracts;
using RankMaster2.Server.Sessions;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// AUDIT THROWAWAY. Two ways a registry can wedge itself: a journal write that throws leaves
/// RenameInProgress set forever; a corrupt journal on open leaks the folder lock.
/// </summary>
public sealed class Audit_RegistryWedgeTests
{
    private static string Folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { "a.jpg", "b.jpg", "c.jpg" })
            File.WriteAllBytes(Path.Combine(dir, n), new byte[] { 1 });
        return dir;
    }

    private static JsonElement Body(string folder) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { folder })).RootElement.Clone();

    private sealed class ThrowingJournalWriter : IRenameJournalWriter
    {
        public void Write(string folder, IReadOnlyList<PlanEntry> plan, DateTimeOffset createdAt) =>
            throw new IOException("disk full");
    }

    [Fact]
    public async Task A_journal_write_that_throws_leaves_the_session_refusing_every_vote()
    {
        var dir = Folder();
        using var registry = new SessionRegistry(journalWriter: new ThrowingJournalWriter());

        var opened = await registry.OpenAsync(Body(dir), CancellationToken.None);
        Assert.False(opened.IsError, opened.ErrorCode);
        var token = opened.Snapshot!.PairToken!;

        // The rename start propagates the IOException (the HTTP layer would turn it into 500 internal_error).
        await Assert.ThrowsAsync<IOException>(() => registry.StartRenameAsync(CancellationToken.None));

        // Now try to vote with the still-valid token.
        var vote = JsonDocument.Parse(JsonSerializer.Serialize(new { pairToken = token, winner = "left" })).RootElement.Clone();
        var voted = await registry.VoteAsync(vote, null, CancellationToken.None);

        // What we would want: the vote goes through (nothing was renamed; the journal never landed).
        // What happens: rename_in_progress, forever, until the session is closed and reopened.
        Assert.False(voted.IsError, $"vote after a failed rename start answered {voted.ErrorCode}");
    }

    [Fact]
    public async Task A_corrupt_journal_on_open_leaks_the_folder_lock()
    {
        var dir = Folder();
        File.WriteAllText(Path.Combine(dir, RenameEngine.JournalFileName), "{ this is not json");
        using var registry = new SessionRegistry();

        // First open: recovery reads the journal and throws (JsonException) out of OpenAsync.
        await Assert.ThrowsAnyAsync<JsonException>(() => registry.OpenAsync(Body(dir), CancellationToken.None));

        // Second open of the same folder: the lock taken before recovery was never disposed.
        var second = await registry.OpenAsync(Body(dir), CancellationToken.None);
        Assert.NotEqual(ErrorCodes.FolderLocked, second.ErrorCode);
    }
}
