using RankMaster2.Pc.Stills;
using Xunit;

namespace RankMaster2.Pc.Stills.Tests;

public class DecodeBudgetTests
{
    [Fact]
    public void Allocating_to_the_ceiling_succeeds_and_the_next_allocation_throws()
    {
        var budget = new DecodeBudget(1024);
        using var first = budget.Allocate(1024);
        Assert.Equal(1024, budget.LiveBytes);

        var ex = Assert.Throws<DecodeBudgetExceededException>(() => budget.Allocate(1));
        Assert.Equal(1, ex.RequestedBytes);
        Assert.Equal(1024, ex.LiveBytes);
        Assert.Equal(1024, ex.CeilingBytes);
    }

    [Fact]
    public void Dispose_refunds_the_budget()
    {
        var budget = new DecodeBudget(1024);
        var buffer = budget.Allocate(600);
        Assert.Equal(600, budget.LiveBytes);

        buffer.Dispose();
        Assert.Equal(0, budget.LiveBytes);

        // The refund makes room for a second allocation the same size.
        using var second = budget.Allocate(600);
        Assert.Equal(600, budget.LiveBytes);
    }

    [Fact]
    public void PeakBytes_records_the_high_water_mark_and_does_not_fall_on_release()
    {
        var budget = new DecodeBudget(1024);
        var a = budget.Allocate(500);
        var b = budget.Allocate(400);
        Assert.Equal(900, budget.PeakBytes);

        a.Dispose();
        Assert.Equal(400, budget.LiveBytes);
        Assert.Equal(900, budget.PeakBytes); // peak does not fall

        b.Dispose();
        budget.ResetPeak();
        Assert.Equal(0, budget.PeakBytes);
    }

    [Fact]
    public void Double_dispose_is_a_no_op()
    {
        var budget = new DecodeBudget(1024);
        var buffer = budget.Allocate(100);
        buffer.Dispose();
        buffer.Dispose();
        Assert.Equal(0, budget.LiveBytes);
    }

    [Fact]
    public void Pointer_throws_after_dispose()
    {
        var budget = new DecodeBudget(1024);
        var buffer = budget.Allocate(100);
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.Pointer);
    }

    [Fact]
    public void A_request_larger_than_the_ceiling_is_refused_immediately()
    {
        var budget = new DecodeBudget(1024);
        Assert.Throws<DecodeBudgetExceededException>(() => budget.Allocate(2000));
        Assert.Equal(0, budget.LiveBytes);
    }
}
