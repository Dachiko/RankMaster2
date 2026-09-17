using System.Text.Json;
using RankMaster2.Catalog;
using RankMaster2.Server.Contracts;
using RankMaster2.Server.Sessions;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// The two ways the registry used to wedge itself, each behind a message that named the wrong cause
/// and each curable only by restarting the server (AUDIT.md H3, H4).
///
/// <para>Both are ordering faults. A rename start marked the session rename-in-progress <i>before</i>
/// writing the journal, so a write that threw left every later vote answering "A rename is already
/// running" with no run to finish it. An open took the folder lock <i>before</i> running journal
/// recovery, outside any catch, so an unreadable journal left the handle open and the folder
/// answering 423 "in use by another Rank Master process" — to the owner himself.</para>
///
/// <para>Driven against <see cref="SessionRegistry"/> directly rather than over HTTP, because both
/// need a failure injected at an exact step (a journal writer that throws, a journal that will not
/// parse) and both are about what the registry's own state is afterwards.</para>
/// </summary>
public sealed class RegistryWedgeTests
{
    private static string Folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-tests", "wedge-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// SERVER_SPEC.md § 10.16, "The flag is set after the journal write, never before": a read-only
    /// folder or a full disk answers <c>500 rename_failed { reunited: true, journal: null }</c> and
    /// leaves the session exactly as it was — the pair on screen still valid, the next call served
    /// normally (H3).
    /// </summary>
    [Fact]
    public async Task A_journal_write_that_throws_answers_rename_failed_and_keeps_the_session_usable()
    {
        var dir = Folder();
        using var registry = new SessionRegistry(journalWriter: new ThrowingJournalWriter());

        var opened = await registry.OpenAsync(Body(dir), CancellationToken.None);
        Assert.False(opened.IsError, opened.ErrorCode);
        var token = opened.Snapshot!.PairToken!;
        var pairSeq = opened.Snapshot!.PairSeq;

        var start = await registry.StartRenameAsync(clientRequestId: null, CancellationToken.None);

        Assert.True(start.IsError, "a journal write that throws cannot be a 202");
        Assert.Equal(ErrorCodes.RenameFailed, start.ErrorCode);
        Assert.Equal(500, ErrorCodes.StatusOf(start.ErrorCode!));

        var details = JsonSerializer.SerializeToElement(start.ErrorDetails);
        Assert.True(details.GetProperty("reunited").GetBoolean(),
            "SERVER_SPEC.md § 10.16: nothing was disturbed and no rating is in doubt, so reunited is true.");
        Assert.Equal(JsonValueKind.Null, details.GetProperty("journal").ValueKind);

        Assert.NotNull(start.ErrorSession);
        Assert.Equal(pairSeq, start.ErrorSession!.PairSeq);

        // The session is untouched, which means the token the client is still holding works.
        var vote = JsonDocument.Parse(
            JsonSerializer.Serialize(new { pairToken = token, winner = "left" })).RootElement.Clone();
        var voted = await registry.VoteAsync(vote, null, CancellationToken.None);

        Assert.False(voted.IsError,
            $"the vote after a failed rename start answered {voted.ErrorCode}. Setting RenameInProgress " +
            "before the journal write is what made every later action say 'A rename is already running' " +
            "with nothing running (AUDIT.md H3).");

        // And a second attempt is a fresh attempt, not "one is already in progress".
        var again = await registry.StartRenameAsync(clientRequestId: null, CancellationToken.None);
        Assert.Equal(ErrorCodes.RenameFailed, again.ErrorCode);

        Assert.False(RenameEngine.JournalExists(dir), "a start that failed leaves no journal behind");
    }

    /// <summary>
    /// SERVER_SPEC.md § 10.16, "An unreadable journal is refused, not guessed at": the open answers
    /// <c>500 rename_failed { reunited: false, journal }</c> and <b>releases the folder lock</b>
    /// before answering, so the folder is openable again the moment the journal is dealt with. The
    /// lock was previously leaked, and the owner was told the folder was in use by another program
    /// until he restarted the server (H4).
    /// </summary>
    [Theory]
    [InlineData("{ this is not json", "truncated JSON")]
    [InlineData("{\"format\":2,\"state\":\"renaming\",\"plan\":null}", "a null plan")]
    [InlineData("{\"format\":7,\"state\":\"renaming\",\"plan\":[]}", "a format this server does not read")]
    public async Task A_corrupt_journal_on_open_answers_rename_failed_and_releases_the_lock(
        string journal, string because)
    {
        var dir = Folder();
        File.WriteAllText(Path.Combine(dir, RenameEngine.JournalFileName), journal);
        using var registry = new SessionRegistry();

        var first = await registry.OpenAsync(Body(dir), CancellationToken.None);

        Assert.True(first.IsError, $"{because}: the session must not open");
        Assert.Equal(ErrorCodes.RenameFailed, first.ErrorCode);

        var details = JsonSerializer.SerializeToElement(first.ErrorDetails);
        Assert.False(details.GetProperty("reunited").GetBoolean(),
            "reunited: false is the truth — the ratings are still in the old database and nothing was touched.");
        Assert.Equal(RenameEngine.JournalPath(dir), details.GetProperty("journal").GetString());

        Assert.False(File.Exists(Path.Combine(dir, ".rankmaster.lock")),
            $"{because}: the folder lock must be released before answering, or every later open of " +
            "this folder answers 423 folder_locked until the server restarts (AUDIT.md H4).");

        // Proved from the other side too: a second open reaches the journal again, not the lock.
        var second = await registry.OpenAsync(Body(dir), CancellationToken.None);
        Assert.NotEqual(ErrorCodes.FolderLocked, second.ErrorCode);
        Assert.Equal(ErrorCodes.RenameFailed, second.ErrorCode);

        // And once the journal is moved aside, the folder opens normally.
        File.Delete(RenameEngine.JournalPath(dir));
        var third = await registry.OpenAsync(Body(dir), CancellationToken.None);
        Assert.False(third.IsError, third.ErrorCode);
    }
}
