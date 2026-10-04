namespace RankMaster2.Pc.Ui.Views;

/// <summary>What one letter of a decoding text is doing at a moment (see <see cref="DecodeTimeline"/>).</summary>
public enum LetterPhase
{
    /// <summary>Its turn has not come: nothing is drawn for it yet.</summary>
    Waiting,
    /// <summary>It flickers through random characters of about its width, at ~40 % of its colour.</summary>
    Noise,
    /// <summary>It has locked in: red at first, easing to its own colour.</summary>
    Locking,
    /// <summary>It is settled: its own character in its own colour.</summary>
    Done,
}

/// <summary>One letter at one moment: the phase, which random character it shows while in
/// <see cref="LetterPhase.Noise"/> (the same one for a whole 30 ms period), and how far its colour has
/// come from red to its own (0..1, eased) while <see cref="LetterPhase.Locking"/>.</summary>
public readonly record struct LetterFrame(LetterPhase Phase, int NoiseSwap, double Lock);

/// <summary>
/// The time-line of Mike's "decode in" (DESIGN.md § 4, "Autofill decodes in"), as plain numbers: left to
/// right, each letter starts 7-14 ms after the one before, flickers through random characters for 110 ms
/// (a new one every 30 ms), locks in red and settles to its own colour over 150 ms, behind a thin red
/// write head that fades over 140 ms after the last letter. About 0.4 s for a folder's name. These are the
/// numbers of <c>Decode</c> in <c>C:\ai\workflows\app-design\wpf\Inline.cs</c> unchanged; this class is
/// the part of the port that needs no drawing, so a test can pin it. <see cref="DecodeLayer"/> draws it.
/// <para/>
/// "Order" is the letter's place among the letters that animate (a text whose unchanged letters stay still,
/// such as a percent going 36 → 37, only animates the changed ones, and the first of them starts at once).
/// </summary>
public sealed class DecodeTimeline
{
    public const double Scramble = 110;
    public const double Swap = 30;
    public const double Settle = 150;
    public const double HeadFade = 140;

    /// <summary>How many letters animate.</summary>
    public int Count { get; }

    /// <summary>Milliseconds between one letter's start and the next one's: 200 ms spread over the
    /// letters, but never less than 7 or more than 14.</summary>
    public double Stagger { get; }

    public DecodeTimeline(int count)
    {
        Count = Math.Max(1, count);
        Stagger = Math.Clamp(200.0 / Count, 7, 14);
    }

    /// <summary>When the last letter has settled (the head starts to fade then).</summary>
    public double LastMs => Stagger * (Count - 1) + Scramble + Settle;

    /// <summary>When everything, the write head included, is over.</summary>
    public double TotalMs => LastMs + HeadFade;

    public bool IsFinished(double ms) => ms > TotalMs;

    public LetterFrame FrameAt(int order, double ms)
    {
        var local = ms - order * Stagger;
        if (local < 0) return new LetterFrame(LetterPhase.Waiting, 0, 0);
        if (local < Scramble) return new LetterFrame(LetterPhase.Noise, (int)(local / Swap), 0);
        var t = Math.Clamp((local - Scramble) / Settle, 0, 1);
        var ease = 1 - Math.Pow(1 - t, 3);
        return new LetterFrame(t >= 1 ? LetterPhase.Done : LetterPhase.Locking, 0, ease);
    }

    /// <summary>The order of the newest letter that has started, or -1 before the first: where the
    /// write head stands.</summary>
    public int HeadOrder(double ms) => ms < 0 ? -1 : Math.Min(Count - 1, (int)(ms / Stagger));

    /// <summary>How much of the write head has faded away, 0 (full) to 1 (gone); it fades only after
    /// the last letter has settled.</summary>
    public double HeadFadeAt(double ms) => Math.Clamp((ms - LastMs) / HeadFade, 0, 1);

    /// <summary>Which letters of <paramref name="after"/> differ from <paramref name="before"/> (all of them
    /// when the lengths differ). The percent uses it: 36 → 37 rolls one digit, 99 → 100 rolls all three.</summary>
    public static bool[] ChangedMask(string? before, string after)
    {
        var mask = new bool[after.Length];
        var same = before is not null && before.Length == after.Length;
        for (var i = 0; i < mask.Length; i++) mask[i] = !same || before![i] != after[i];
        return mask;
    }
}

/// <summary>
/// "Motion answers the user, not the machine" (DESIGN.md § 4): a change counts as the user's when it
/// lands within 1.5 s of a key press or a click. Everything else -- a link status that flips, a refresh --
/// lands still. The clock is a parameter so a test can run it without waiting.
/// </summary>
public sealed class UserActivity
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1500);

    private readonly Func<TimeSpan> _now;
    private TimeSpan? _last;

    public UserActivity(Func<TimeSpan>? now = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _now = now ?? (() => clock.Elapsed);
    }

    /// <summary>A key or a click just happened.</summary>
    public void Note() => _last = _now();

    /// <summary>True when something the user did is no more than <see cref="Window"/> old.</summary>
    public bool Recent => _last is { } last && _now() - last <= Window;
}
