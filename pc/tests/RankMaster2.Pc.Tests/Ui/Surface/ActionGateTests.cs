using RankMaster2.Pc.Ui.Surface;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>Plan § 6.1 "ActionGate": the five rules of § 3.1, every combination.</summary>
public class ActionGateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static GateInputs Ready(Intent intent, DateTimeOffset now, DateTimeOffset arrivedAt) => new(
        Busy: false,
        PanesReady: true,
        KeyReleasedSinceLastAccept: true,
        Now: now,
        PairArrivedAt: arrivedAt,
        RequestedPairSeq: 1,
        CurrentPairSeq: 1);

    [Fact]
    public void Busy_drops_everything()
    {
        var inputs = Ready(Intent.VoteLeft, T0 + Timings.ArrivalGuardMs, T0) with { Busy = true };
        Assert.Equal(GateVerdict.Drop(DropReason.Busy), ActionGate.Try(Intent.VoteLeft, inputs));
    }

    [Fact]
    public void Not_ready_drops_a_pair_action()
    {
        var inputs = Ready(Intent.VoteLeft, T0 + Timings.ArrivalGuardMs, T0) with { PanesReady = false };
        Assert.Equal(GateVerdict.Drop(DropReason.NotReady), ActionGate.Try(Intent.VoteLeft, inputs));
    }

    [Fact]
    public void Held_key_is_one_accept_until_release()
    {
        var inputs = Ready(Intent.VoteLeft, T0 + Timings.ArrivalGuardMs, T0) with { KeyReleasedSinceLastAccept = false };
        Assert.Equal(GateVerdict.Drop(DropReason.KeyStillHeld), ActionGate.Try(Intent.VoteLeft, inputs));
    }

    [Fact]
    public void Nothing_accepted_within_the_arrival_guard()
    {
        var now = T0 + Timings.ArrivalGuardMs - TimeSpan.FromMilliseconds(1);
        var inputs = Ready(Intent.VoteLeft, now, T0);
        Assert.Equal(GateVerdict.Drop(DropReason.ArrivalGuard), ActionGate.Try(Intent.VoteLeft, inputs));
    }

    [Fact]
    public void Accepted_exactly_at_the_arrival_guard()
    {
        var now = T0 + Timings.ArrivalGuardMs;
        var inputs = Ready(Intent.VoteLeft, now, T0);
        Assert.True(ActionGate.Try(Intent.VoteLeft, inputs).IsAccepted);
    }

    [Fact]
    public void Accepted_one_ms_after_the_arrival_guard()
    {
        var now = T0 + Timings.ArrivalGuardMs + TimeSpan.FromMilliseconds(1);
        var inputs = Ready(Intent.VoteLeft, now, T0);
        Assert.True(ActionGate.Try(Intent.VoteLeft, inputs).IsAccepted);
    }

    [Fact]
    public void Token_mismatch_drops()
    {
        var inputs = Ready(Intent.VoteLeft, T0 + Timings.ArrivalGuardMs, T0) with { RequestedPairSeq = 1, CurrentPairSeq = 2 };
        Assert.Equal(GateVerdict.Drop(DropReason.TargetMismatch), ActionGate.Try(Intent.VoteLeft, inputs));
    }

    // ---- undo / save: busy + release only, no pane-ready rule and no arrival guard --------------------

    [Fact]
    public void Undo_and_save_ignore_pane_readiness()
    {
        var notReady = Ready(Intent.Undo, T0, T0) with { PanesReady = false };
        Assert.True(ActionGate.Try(Intent.Undo, notReady).IsAccepted);
        Assert.True(ActionGate.Try(Intent.Save, notReady).IsAccepted);
    }

    [Fact]
    public void Undo_and_save_ignore_the_arrival_guard()
    {
        // "now" is before the pair even arrived by the model's clock -- rule 4 would drop a pair
        // action here, but plan § 1.1 marks Undo/Save "busy only".
        var inputs = Ready(Intent.Undo, T0, T0 + TimeSpan.FromSeconds(1));
        Assert.True(ActionGate.Try(Intent.Undo, inputs).IsAccepted);
        Assert.True(ActionGate.Try(Intent.Save, inputs).IsAccepted);
    }

    [Fact]
    public void Undo_and_save_still_respect_busy_and_release()
    {
        var busy = Ready(Intent.Undo, T0, T0) with { Busy = true };
        Assert.Equal(GateVerdict.Drop(DropReason.Busy), ActionGate.Try(Intent.Undo, busy));

        var held = Ready(Intent.Save, T0, T0) with { KeyReleasedSinceLastAccept = false };
        Assert.Equal(GateVerdict.Drop(DropReason.KeyStillHeld), ActionGate.Try(Intent.Save, held));
    }

    [Fact]
    public void A_dropped_mouse_click_is_always_released()
    {
        // Mouse clicks report KeyReleasedSinceLastAccept: true always (RankModel does this by
        // passing key: null); a busy click is still dropped on rule 1.
        var inputs = Ready(Intent.VoteLeft, T0 + Timings.ArrivalGuardMs, T0) with { Busy = true, KeyReleasedSinceLastAccept = true };
        Assert.Equal(GateVerdict.Drop(DropReason.Busy), ActionGate.Try(Intent.VoteLeft, inputs));
    }
}
