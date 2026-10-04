using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RankMaster2.Pc.Link;
using RankMaster2.Pc.Ui.Surface;
using RankMaster2.Pc.Ui.Tests.Fakes;
using RankMaster2.Pc.Ui.Views;
using Xunit;

namespace RankMaster2.Pc.Ui.Tests.Views;

/// <summary>
/// Plan H § 5 S3 (motion). The pure half: the decode's time-line numbers and the "is this change the
/// owner's" rule. The headless half: what the decode layer does to the text it covers, the fades, and
/// that a change nobody asked for lands still while one the owner just made decodes in. Nothing is drawn
/// or shown on screen.
/// </summary>
public class HouseMotionTests
{
    // ---- DecodeTimeline: Mike's numbers (DESIGN.md § 4), unchanged from Decode.In in Inline.cs ------------

    [Fact]
    public void A_folder_name_decodes_in_about_four_tenths_of_a_second_then_the_head_fades()
    {
        var t = new DecodeTimeline(12); // ICELAND 2026 is 12 letters
        Assert.Equal(14, t.Stagger);                                  // 200 / 12 = 16.7, capped at 14
        Assert.Equal(14 * 11 + 110 + 150, t.LastMs);                  // 414 ms: the last letter has settled
        Assert.InRange(t.LastMs, 380, 440);
        Assert.Equal(t.LastMs + 140, t.TotalMs);                      // the write head fades over 140 ms
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(10, 14)]
    [InlineData(20, 10)]
    [InlineData(40, 7)]
    [InlineData(200, 7)]
    public void Letters_start_7_to_14_ms_apart(int count, double stagger) =>
        Assert.Equal(stagger, new DecodeTimeline(count).Stagger);

    [Fact]
    public void A_letter_waits_flickers_locks_in_red_then_settles()
    {
        var t = new DecodeTimeline(5); // stagger 14 -> letter 2 starts at 28 ms
        Assert.Equal(LetterPhase.Waiting, t.FrameAt(2, 27).Phase);
        Assert.Equal(LetterPhase.Noise, t.FrameAt(2, 28).Phase);
        Assert.Equal(LetterPhase.Noise, t.FrameAt(2, 28 + 109).Phase);

        var locking = t.FrameAt(2, 28 + 110);
        Assert.Equal(LetterPhase.Locking, locking.Phase);
        Assert.Equal(0, locking.Lock, 6);                              // starts at red
        var middle = t.FrameAt(2, 28 + 110 + 75);
        Assert.Equal(LetterPhase.Locking, middle.Phase);
        Assert.InRange(middle.Lock, 0.8, 0.9);                         // ease-out: most of the way at half time
        Assert.Equal(LetterPhase.Done, t.FrameAt(2, 28 + 110 + 150).Phase);
    }

    [Fact]
    public void A_flickering_letter_keeps_one_random_character_for_30_ms_then_takes_another_period()
    {
        var t = new DecodeTimeline(3);
        Assert.Equal(0, t.FrameAt(0, 0).NoiseSwap);
        Assert.Equal(0, t.FrameAt(0, 29).NoiseSwap);
        Assert.Equal(1, t.FrameAt(0, 30).NoiseSwap);
        Assert.Equal(3, t.FrameAt(0, 109).NoiseSwap);
    }

    [Fact]
    public void The_write_head_follows_the_newest_letter_and_fades_only_after_the_last_one_settles()
    {
        var t = new DecodeTimeline(4); // stagger 14
        Assert.Equal(-1, t.HeadOrder(-1));
        Assert.Equal(0, t.HeadOrder(0));
        Assert.Equal(1, t.HeadOrder(14));
        Assert.Equal(3, t.HeadOrder(1000)); // never past the last letter
        Assert.Equal(0, t.HeadFadeAt(t.LastMs));
        Assert.Equal(0.5, t.HeadFadeAt(t.LastMs + 70), 6);
        Assert.Equal(1, t.HeadFadeAt(t.TotalMs + 500));
        Assert.False(t.IsFinished(t.TotalMs));
        Assert.True(t.IsFinished(t.TotalMs + 1));
    }

    [Theory]
    [InlineData("36", "37", "01")]      // the percent rolls only the digit that changed
    [InlineData("99", "100", "111")]    // a different length: every digit rolls
    [InlineData("5", "5", "0")]
    [InlineData(null, "12", "11")]      // nothing before: everything rolls
    public void Only_the_letters_that_changed_roll(string? before, string after, string expected) =>
        Assert.Equal(expected, string.Concat(DecodeTimeline.ChangedMask(before, after).Select(b => b ? '1' : '0')));

    // ---- UserActivity: "motion answers the user, not the machine" ---------------------------------------

    [Fact]
    public void A_change_within_a_second_and_a_half_of_a_key_or_click_is_the_owners()
    {
        var now = TimeSpan.FromSeconds(100);
        var activity = new UserActivity(() => now);
        Assert.False(activity.Recent);          // nothing has happened yet: app start lands still

        activity.Note();
        Assert.True(activity.Recent);
        now += TimeSpan.FromMilliseconds(1499);
        Assert.True(activity.Recent);
        now += TimeSpan.FromMilliseconds(2);
        Assert.False(activity.Recent);          // a status that flips by itself later does not decode
    }

    // ---- HouseMotion.Change --------------------------------------------------------------------------

    private static (DecodeLayer Layer, TextBlock Text, HouseMotion Motion, Window Window, FakeClockMs Clock) Rig()
    {
        var clock = new FakeClockMs();
        var layer = new DecodeLayer(clock.Now);
        var text = new TextBlock { FontSize = 20 };
        var window = new Window { Width = 400, Height = 200, Content = new Grid { Children = { text, layer } } };
        window.Show();
        return (layer, text, new HouseMotion(layer, new UserActivity(() => TimeSpan.Zero)), window, clock);
    }

    private sealed class FakeClockMs
    {
        public TimeSpan Elapsed;
        public TimeSpan Now() => Elapsed;
        public void Advance(double ms) => Elapsed += TimeSpan.FromMilliseconds(ms);
    }

    [AvaloniaFact]
    public void A_changed_text_decodes_only_when_the_owner_caused_it()
    {
        var (layer, text, motion, _, _) = Rig();

        motion.Change(text, "FIRST", userCaused: false);
        Assert.Equal("FIRST", text.Text);
        Assert.Equal(0, layer.Playing);
        Assert.Equal(1, text.Opacity);

        motion.Change(text, "SECOND", userCaused: true);
        Assert.Equal("SECOND", text.Text);    // the text is already its final text: layout cannot move
        Assert.Equal(1, layer.Playing);
        Assert.Equal(0, text.Opacity);        // hidden by opacity until the letters have settled
    }

    [AvaloniaFact]
    public void The_same_text_again_does_not_decode_and_does_not_restart_one_that_is_running()
    {
        var (layer, text, motion, _, _) = Rig();
        motion.Change(text, "ABC", userCaused: true);
        Assert.Equal(1, layer.Playing);

        motion.Change(text, "ABC", userCaused: true);
        Assert.Equal(1, layer.Playing);
    }

    [AvaloniaFact]
    public void A_decode_ends_by_itself_and_shows_the_text_again()
    {
        var (layer, text, motion, _, clock) = Rig();
        motion.Change(text, "ICELAND 2026", userCaused: true);

        clock.Advance(300);
        layer.Tick();
        Assert.Equal(1, layer.Playing);
        Assert.Equal(0, text.Opacity);

        clock.Advance(300); // past LastMs + head fade (554 ms)
        layer.Tick();
        Assert.Equal(0, layer.Playing);
        Assert.Equal(1, text.Opacity);
    }

    [AvaloniaFact]
    public void FinishAll_ends_every_decode_at_once()
    {
        var (layer, text, motion, _, _) = Rig();
        motion.Change(text, "ICELAND 2026", userCaused: true);
        Assert.Equal(1, layer.Playing);

        layer.FinishAll();

        Assert.Equal(0, layer.Playing);
        Assert.Equal(1, text.Opacity);
        Assert.Equal("ICELAND 2026", text.Text);
    }

    [AvaloniaFact]
    public void A_new_decode_of_the_same_text_ends_the_old_one_and_keeps_the_opacity_it_had()
    {
        var (layer, text, motion, _, _) = Rig();
        text.Opacity = 0.5;
        motion.Change(text, "ONE", userCaused: true);
        motion.Change(text, "TWO", userCaused: true);

        Assert.Equal(1, layer.Playing);
        layer.FinishAll();
        Assert.Equal(0.5, text.Opacity); // restored to its own, not to the 0 the first play had set
    }

    [AvaloniaFact]
    public void A_percent_going_36_to_37_rolls_one_digit_and_a_text_that_is_not_visible_just_shows()
    {
        var (layer, text, motion, _, _) = Rig();
        motion.Change(text, "36", userCaused: false);
        motion.Change(text, "37", userCaused: true, onlyChangedLetters: true);
        Assert.Equal(1, layer.Playing);
        layer.FinishAll();

        text.IsVisible = false;
        motion.Change(text, "38", userCaused: true, onlyChangedLetters: true);
        Assert.Equal(0, layer.Playing);
        Assert.Equal("38", text.Text);
    }

    [AvaloniaFact]
    public void An_empty_text_has_nothing_to_decode()
    {
        var (layer, text, motion, _, _) = Rig();
        motion.Change(text, "ABC", userCaused: false);
        motion.Change(text, "", userCaused: true);
        Assert.Equal(0, layer.Playing);
        Assert.Equal("", text.Text);
    }

    // ---- fades (plan H § 5 S3: ~150 ms, ease-out) ------------------------------------------------------

    [AvaloniaFact]
    public async Task A_fade_host_shows_at_once_and_hides_after_the_fade_without_taking_input_meanwhile()
    {
        var control = new Border { IsVisible = false };
        var window = new Window { Content = new Grid { Children = { control } } };
        window.Show();
        var fade = new FadeHost(control);
        Assert.False(fade.IsShown);

        fade.Show();
        Assert.True(fade.IsShown);
        Assert.True(control.IsVisible);
        Assert.True(control.IsHitTestVisible);
        await Task.Delay(400); // the 150 ms ease-out has run
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, control.Opacity, 2);

        fade.Hide();
        Assert.False(fade.IsShown);
        Assert.False(control.IsHitTestVisible);   // no input from the moment it starts to fade
        Assert.True(control.IsVisible);           // still drawn for the fade

        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.False(control.IsVisible);
    }

    [AvaloniaFact]
    public async Task Showing_again_during_a_hide_cancels_the_hide()
    {
        var control = new Border { IsVisible = false };
        var window = new Window { Content = new Grid { Children = { control } } };
        window.Show();
        var fade = new FadeHost(control);
        fade.Show();
        fade.Hide();
        fade.Show();

        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.True(control.IsVisible);
        Assert.True(fade.IsShown);
    }

    [Fact]
    public void The_fade_is_150_ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), FadeHost.Duration);
    }

    // ---- through the real start screen ---------------------------------------------------------------

    private static (Window Window, UiRoot Root, RankCoordinator Coordinator, FakeSessionLink Link) Build()
    {
        var link = new FakeSessionLink { Snapshot = SnapshotBuilder.Ranking() };
        var store = new LastFolderStore(Path.Combine(Path.GetTempPath(), "rm2-house-motion-tests-" + Guid.NewGuid().ToString("N"), "last-folder.txt"));
        var coordinator = new RankCoordinator(link, new FakeStillSource(), null, new FakeClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1)),
            new SynchronousUiThread(), new ImmediateDelay(), store);
        var root = new UiRoot(coordinator);
        var window = new Window { Content = root, Width = 1280, Height = 720 };
        window.Show();
        coordinator.InitializeAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        return (window, root, coordinator, link);
    }

    private static T? Find<T>(Control root, string? name = null) where T : Control
    {
        if (root is T match && (name is null || root.Name == name)) return match;
        foreach (var child in root.GetVisualChildren())
            if (child is Control c && Find<T>(c, name) is { } found) return found;
        return null;
    }

    [AvaloniaFact]
    public void A_status_that_changes_by_itself_lands_still()
    {
        var (_, root, c, link) = Build();
        var start = Find<StartView>(root)!;
        var layer = Find<DecodeLayer>(root)!;
        Assert.Equal(0, layer.Playing); // the first paint at start-up did not decode either

        // The server goes away with nobody touching anything: the lamp and the status line change, still.
        link.State = LinkState.Disconnected;
        link.LastFailure = new Failure(FailureKind.ServerNotRunning, "x", "", "client_unreachable", null, Fatal: false);
        start.Refresh();

        Assert.Equal("Server not answering", Find<TextBlock>(start, "StatusLine")!.Text);
        Assert.Equal(0, layer.Playing);
        Assert.Equal(1, Find<TextBlock>(start, "StatusLine")!.Opacity);
    }

    [AvaloniaFact]
    public void A_hero_that_changes_at_the_owners_key_decodes_and_the_next_key_ends_it()
    {
        var (window, root, c, _) = Build();
        var start = Find<StartView>(root)!;
        var layer = Find<DecodeLayer>(root)!;
        var hero = Find<TextBlock>(start, "HeroText")!;

        window.KeyPress(Key.Right, RawInputModifiers.None); // the owner acts
        c.Start.BeginOpening("/photos/Iceland");            // ... and what he did changes the hero
        start.Refresh();

        Assert.Equal("ICELAND", hero.Text);
        Assert.True(layer.Playing >= 1);
        Assert.Equal(0, hero.Opacity);

        window.KeyPress(Key.Left, RawInputModifiers.None);  // any key ends a running animation
        Assert.Equal(0, layer.Playing);
        Assert.Equal(1, hero.Opacity);
    }

    [AvaloniaFact]
    public void The_ink_card_is_shown_and_hidden_through_the_fade()
    {
        var (_, root, c, _) = Build();
        var card = Find<RenameView>(root)!;

        c.BeginRenameConfirm("/lib/photos");
        Dispatcher.UIThread.RunJobs();
        Assert.True(card.IsShown);
        Assert.True(card.IsVisible);

        c.CancelRenameConfirm();
        Dispatcher.UIThread.RunJobs();
        Assert.False(card.IsShown);
        Assert.False(card.IsHitTestVisible);
    }
}
