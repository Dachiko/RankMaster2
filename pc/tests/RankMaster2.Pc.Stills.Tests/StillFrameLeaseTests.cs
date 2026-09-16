using RankMaster2.Pc.Stills;
using Xunit;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// StillFrame's constructor, Lease() and Release() are internal (plan section 3.6: only the
/// cache/source hand out leases) and reachable here because this test assembly is named a friend
/// via [InternalsVisibleTo] in AssemblyInfo.cs.
/// </summary>
public class StillFrameLeaseTests
{
    private static (StillFrame Frame, DecodeBudget Budget) MakeFrame(long bytes = 64)
    {
        var budget = new DecodeBudget(1024 * 1024);
        var buffer = budget.Allocate(bytes);
        var frame = new StillFrame("id-1", buffer, 4, 4, 4, 4, isPartial: false);
        return (frame, budget);
    }

    [Fact]
    public void Cache_reference_dropped_while_a_lease_is_alive_leaves_Pixels_valid()
    {
        var (frame, budget) = MakeFrame();
        var lease = frame.Lease();

        // The cache lets go of its own reference (eviction) while E's lease is still outstanding.
        frame.Release();

        // The buffer must still be valid: E's lease is alive.
        _ = frame.Pixels;
        Assert.True(budget.LiveBytes > 0);

        lease.Dispose();
        Assert.Equal(0, budget.LiveBytes);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void Two_leases_disposed_in_either_order_free_exactly_once()
    {
        var (frame, budget) = MakeFrame();
        var lease1 = frame.Lease();
        var lease2 = frame.Lease();
        frame.Release(); // cache's own reference gone; two leases keep it alive

        lease2.Dispose();
        Assert.True(budget.LiveBytes > 0); // lease1 still alive
        _ = frame.Pixels;

        lease1.Dispose();
        Assert.Equal(0, budget.LiveBytes);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void Leases_disposed_in_the_opposite_order_also_free_exactly_once()
    {
        var (frame, budget) = MakeFrame();
        var lease1 = frame.Lease();
        var lease2 = frame.Lease();
        frame.Release();

        lease1.Dispose();
        Assert.True(budget.LiveBytes > 0); // lease2 still alive

        lease2.Dispose();
        Assert.Equal(0, budget.LiveBytes);
    }

    [Fact]
    public void A_lease_disposed_twice_is_a_no_op()
    {
        var (frame, budget) = MakeFrame();
        var lease = frame.Lease();
        frame.Release();

        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, budget.LiveBytes);
    }

    [Fact]
    public void ByteSize_is_RowBytes_times_Height()
    {
        var budget = new DecodeBudget(1024 * 1024);
        var buffer = budget.Allocate(10 * 4 * 20);
        var frame = new StillFrame("id-2", buffer, 10, 20, 10, 20, isPartial: false);

        Assert.Equal(40, frame.RowBytes);
        Assert.Equal(800, frame.ByteSize);
        frame.Release();
    }
}
