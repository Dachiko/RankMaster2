using System.Text.Json;
using RankMaster2.Catalog;
using RankMaster2.Server.Contracts;
using RankMaster2.Server.Sessions;
using Xunit;

namespace RankMaster2.Server.Tests;

/// <summary>
/// AUDIT2.md § 2.2: a rename that hits a file it cannot move used to wedge the session for ever.
///
/// <para><see cref="SessionRegistry.RunRenameAsync"/> runs fire-and-forget
/// (<c>_ = Task.Run(...)</c>). Its recovery path — <c>RenameEngine.RecoverIfPresent</c>, via
/// <c>FinalizeBestEffort</c> — used to catch only <c>IOException</c>, so an
/// <c>UnauthorizedAccessException</c> (an ACL-denied file or folder; on Windows the realistic
/// trigger the owner would hit on a slow USB drive with odd permissions) walked straight out of
/// every catch that was supposed to stop it, faulted the unobserved background task, and left
/// <c>open.RenameInProgress</c> set for the life of the process: every vote, skip, discard and undo
/// answered <c>409 rename_in_progress</c>, cancel could flip the state to "cancelling" and nothing
/// further ever happened, and only restarting the server cured it.</para>
///
/// <para>Reproduced here the same way <see cref="RegistryWedgeTests"/> reproduces the sibling H3/H4
/// wedges: driving <see cref="SessionRegistry"/> directly, with a custom
/// <see cref="IRenameJournalWriter"/> that writes the journal normally and then, in the same call
/// (i.e. before any file move is attempted — the same "between the journal write and the first
/// move" window AUDIT2.md describes), drops write permission on the folder. On Linux, a move into a
/// directory the process cannot write throws <see cref="UnauthorizedAccessException"/>, exactly like
/// an ACL denial on Windows — no production code is touched to force this, only the existing DI
/// seam.</para>
/// </summary>
public sealed class RenameAccessDeniedWedgeTests
{
    private static string Folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rm2-tests", "wedge-uae-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { "a.jpg", "b.jpg", "c.jpg", "d.jpg" })
            File.WriteAllBytes(Path.Combine(dir, n), new byte[] { 1 });
        return dir;
    }

    private static JsonElement Body(string folder) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { folder })).RootElement.Clone();

    private static void MakeReadOnly(string folder) =>
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

    private static void MakeWritable(string folder) =>
        File.SetUnixFileMode(folder,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>Writes the journal exactly as production does, then makes the folder unwritable
    /// before returning — so the very first move the background task attempts, and every move any
    /// recovery attempt retries, throws <see cref="UnauthorizedAccessException"/> rather than the
    /// <see cref="IOException"/> the retry loop already tolerates.</summary>
    private sealed class AccessDeniedAfterJournalWriter : IRenameJournalWriter
    {
        public void Write(string folder, IReadOnlyList<PlanEntry> plan, DateTimeOffset createdAt)
        {
            RenameEngine.WriteJournal(folder, plan, createdAt);
            MakeReadOnly(folder);
        }
    }

    [Fact]
    public async Task Cancel_clears_the_wedge_and_the_session_takes_votes_again()
    {
        var dir = Folder();
        try
        {
            using var registry = new SessionRegistry(journalWriter: new AccessDeniedAfterJournalWriter());

            var opened = await registry.OpenAsync(Body(dir), CancellationToken.None);
            Assert.False(opened.IsError, opened.ErrorCode);

            var start = await registry.StartRenameAsync(clientRequestId: null, CancellationToken.None);
            Assert.False(start.IsError,
                $"the start itself must still be accepted (202) — the folder only becomes unwritable " +
                $"after the journal write, inside it — but got {start.ErrorCode}");

            // Give the fire-and-forget background task (Task.Run) a real chance to be scheduled and
            // attempt — and fail — its first move before we ask for cancel. Racing cancel in
            // immediately, before MoveAll's shouldStop check ever runs, would abort cleanly before
            // any move (§ 3.5 "preparing") and never touch the bug this test exists to catch.
            await Task.Delay(300);

            // Now ask for cancel. Whether the background task is still stuck mid-move or has
            // already reached a terminus on its own by the time this lands does not matter: either
            // way cancel must not be left watching "cancelling" for ever.
            var cancel = await registry.CancelRenameAsync(CancellationToken.None);
            Assert.False(cancel.IsError, cancel.ErrorCode);

            // Poll GET /session/rename to a terminal state. Before the fix, this loop timed out —
            // the operation sat at "cancelling" (or "running") for ever, because the background
            // task had faulted, unobserved, inside its own recovery attempt.
            RenameOutcome? last = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var get = await registry.GetRenameAsync(CancellationToken.None);
                Assert.False(get.IsError, get.ErrorCode);
                last = get;
                if (get.Operation!.State is "succeeded" or "cancelled" or "failed")
                    break;
                await Task.Delay(15);
            }

            Assert.True(last is not null && last.Operation!.State is "succeeded" or "cancelled" or "failed",
                $"the rename never reached a terminal state within the deadline — the session is wedged " +
                $"(AUDIT2.md § 2.2). Last observed state: {last?.Operation?.State ?? "<none>"}");

            // Plug the slow USB drive back in — restore write access — and prove the session is not
            // merely reporting a terminus but is actually usable again: a fresh vote must succeed.
            MakeWritable(dir);

            var snapshot = await registry.ReadAsync(CancellationToken.None);
            Assert.False(snapshot.IsError, snapshot.ErrorCode);
            var token = snapshot.Snapshot!.PairToken;
            Assert.False(string.IsNullOrEmpty(token), "the session must still be able to offer a pair to vote on");

            var voteBody = JsonDocument.Parse(
                JsonSerializer.Serialize(new { pairToken = token, winner = "left" })).RootElement.Clone();
            var voted = await registry.VoteAsync(voteBody, null, CancellationToken.None);

            Assert.False(voted.IsError,
                $"a vote after cancel still answered {voted.ErrorCode} — cancel did not clear the wedge " +
                "(AUDIT2.md § 2.2). Recovering only fixes the on-disk files; it is worthless to the owner " +
                "unless the session that survives actually takes votes again.");
        }
        finally
        {
            try { MakeWritable(dir); } catch { /* best effort cleanup */ }
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }
}
