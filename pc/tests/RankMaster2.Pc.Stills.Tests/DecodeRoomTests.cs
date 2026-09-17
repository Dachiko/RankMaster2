// AUDIT2.md § 2.1 on this side of the product: two visible decodes run at once
// (DecodeQueue's two workers) against one DecodeBudget, and pc/plans/C-stills.md § 3.3's rule for
// the second one -- "if live + n > Ceiling and live > 0 -> wait ... and retry" -- was never written.
// Without it the second of a pair is refused for want of room and the owner is told his photograph
// cannot be decoded. DecodeRoom is that rule; these are its terms, deterministically.
using RankMaster2.Pc.Stills;
using Xunit;

namespace RankMaster2.Pc.Stills.Tests;

public class DecodeRoomTests
{
    [Fact]
    public void A_decode_that_fits_is_admitted_without_waiting()
    {
        var budget = new DecodeBudget(1000);
        var room = new DecodeRoom(budget);

        using var reservation = room.Take(600);

        Assert.Equal(600, reservation.Bytes);
        Assert.Equal(600, room.ReservedBytes);
        Assert.Equal(0, room.WaitedAdmissions);
        Assert.True(room.LastWait < TimeSpan.FromMilliseconds(50), $"admitted after an unexpected wait of {room.LastWait}");
    }

    [Fact]
    public void A_reservation_returns_its_room_when_it_is_disposed()
    {
        var budget = new DecodeBudget(1000);
        var room = new DecodeRoom(budget);

        var first = room.Take(900);
        Assert.Equal(900, room.ReservedBytes);
        first.Dispose();
        Assert.Equal(0, room.ReservedBytes);

        using var second = room.Take(900); // the room is free again, so this is immediate
        Assert.Equal(900, room.ReservedBytes);
    }

    [Fact]
    public void Bytes_already_live_in_the_budget_count_against_a_new_reservation()
    {
        // Cached frames are live budget bytes that belong to no decode. They have to count, or the
        // reservation would promise room the frames are already using.
        var budget = new DecodeBudget(1000);
        var room = new DecodeRoom(budget, maxWait: TimeSpan.FromMilliseconds(50));
        using var cachedFrame = budget.Allocate(700);

        using var fits = room.Take(300);
        Assert.Throws<DecodeBudgetExceededException>(() => room.Take(1));
    }

    [Fact]
    public async Task The_second_of_a_pair_waits_for_the_first_instead_of_being_refused()
    {
        // 600 + 600 does not fit in 1000: exactly the collision two big PNGs make. The point of the
        // finding is that the second decode must not fail -- it must wait its turn.
        var budget = new DecodeBudget(1000);
        var room = new DecodeRoom(budget);

        var first = room.Take(600);

        var second = Task.Run(() => room.Take(600));
        Assert.False(second.Wait(TimeSpan.FromMilliseconds(150)), "the second decode should still be waiting, not refused");

        first.Dispose();

        using var admitted = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(600, admitted.Bytes);
        Assert.Equal(1, room.WaitedAdmissions);
        Assert.True(room.LastWait > TimeSpan.Zero, "the second decode was admitted without ever having waited");
    }

    [Fact]
    public void A_decode_bigger_than_the_whole_ceiling_is_refused_at_once_and_never_waits()
    {
        // The deliberate, documented ceiling (§ 3.3's 200 MP PNG). Waiting cannot make room that does
        // not exist, so this must not cost the owner ten seconds of spinner first.
        var budget = new DecodeBudget(1000);
        var room = new DecodeRoom(budget, maxWait: TimeSpan.FromSeconds(30));

        var ex = Assert.Throws<DecodeBudgetExceededException>(() => room.Take(1001));

        Assert.Equal(1001, ex.RequestedBytes);
        Assert.Equal(1000, ex.CeilingBytes);
        Assert.True(room.LastWait < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_wait_that_runs_out_refuses_rather_than_blocking_for_ever()
    {
        // The backstop: StillSource.ReleaseAllAsync waits for in-flight decodes, so a wait with no
        // end would be a folder that never closed.
        var budget = new DecodeBudget(1000);
        var room = new DecodeRoom(budget, maxWait: TimeSpan.FromMilliseconds(100));
        using var held = room.Take(900);

        Assert.Throws<DecodeBudgetExceededException>(() => room.Take(900));
        Assert.True(room.LastWait >= TimeSpan.FromMilliseconds(90), $"gave up after only {room.LastWait}");
    }

    [Fact]
    public async Task Reservations_queue_rather_than_overlapping_when_several_decodes_collide()
    {
        var budget = new DecodeBudget(1000);
        var room = new DecodeRoom(budget, maxWait: TimeSpan.FromSeconds(10));
        var peak = 0L;
        var gate = new object();

        async Task Decode()
        {
            using var reservation = room.Take(400);
            lock (gate) peak = Math.Max(peak, room.ReservedBytes);
            await Task.Delay(20);
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(Decode)));

        Assert.True(peak <= 800, $"reservations overlapped past the ceiling: {peak}");
        Assert.Equal(0, room.ReservedBytes);
    }
}
