using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// Plan H § 5 S3: Mike's "decode in" for Avalonia, a port of <c>Decode.In</c> from
/// <c>C:\ai\workflows\app-design\wpf\Inline.cs</c>. WPF draws the letters in an adorner over the element;
/// Avalonia has no adorners and <c>TextBlock.Render</c> is sealed, so this is one transparent control that
/// sits over everything in <see cref="UiRoot"/> and draws, for every text that is decoding, its letters at
/// the places the text's own layout gives them. The text itself is already its final text (so layout never
/// moves) and is hidden by its opacity until the last letter has settled, then shown again.
/// <para/>
/// Left to right, each letter flickers through 3-4 random characters of about its width (a digit through
/// digits) at ~40 % of its colour, locks in red and settles to its own colour, behind a thin red write head
/// (<see cref="DecodeTimeline"/> holds the numbers). One decode at a time per text: a new one ends the old.
/// <see cref="FinishAll"/> ends every one at once (any key or click: the owner never waits on an animation).
/// A text that stops being visible just shows. The overlay is drawn at the product of the text's own and its
/// ancestors' opacity, so a decode inside the fading ink card fades with it.
/// <para/>
/// Not hit-testable, not focusable.
/// </summary>
public sealed class DecodeLayer : Control
{
    private const string NoiseLetters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnopqrstuvwxyz#$%&*+=<>?/\\@";
    private const string NoiseDigits = "0123456789";

    private static readonly Dictionary<(Typeface, double, bool), (char C, double W)[]> Pools = [];

    private sealed class Play
    {
        public required TextBlock Target;
        public required string Text;
        public required Rect[] Cells;       // each letter's place in the target's own coordinates
        public required int[] Order;        // letter -> its place among the animating letters, or -1 (static)
        public required DecodeTimeline Timeline;
        public required double OwnOpacity;
        public required int Seed;
        public required double StartedMs;
        public required Typeface Face;
        public required double Size;
    }

    private readonly List<Play> _plays = [];
    private readonly Func<TimeSpan> _clock;
    private DispatcherTimer? _timer;

    public DecodeLayer() : this(null) { }

    /// <param name="clock">Elapsed time since any fixed moment; a test passes its own.</param>
    public DecodeLayer(Func<TimeSpan>? clock)
    {
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>How many decodes are running.</summary>
    public int Playing => _plays.Count;

    /// <summary>
    /// Starts decoding <paramref name="target"/>'s current text. With <paramref name="animate"/> only the
    /// letters it marks decode and the rest stay as they are (a percent going 36 → 37 rolls its last digit
    /// only); without it every letter decodes. Does nothing for an empty or invisible text.
    /// </summary>
    public void In(TextBlock target, bool[]? animate = null)
    {
        Finish(target);
        var text = target.Text ?? "";
        if (text.Length == 0 || !target.IsEffectivelyVisible) return;

        var order = new int[text.Length];
        var next = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var animated = !char.IsWhiteSpace(text[i]) && (animate is null || (i < animate.Length && animate[i]));
            order[i] = animated ? next++ : -1;
        }
        if (next == 0) return;

        _plays.Add(new Play
        {
            Target = target,
            Text = text,
            Cells = CellsOf(target, text),
            Order = order,
            Timeline = new DecodeTimeline(next),
            OwnOpacity = target.Opacity,
            Seed = Random.Shared.Next(),
            StartedMs = _clock().TotalMilliseconds,
            Face = new Typeface(target.FontFamily, target.FontStyle, target.FontWeight, target.FontStretch),
            Size = target.FontSize,
        });
        target.Opacity = 0;

        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();
        InvalidateVisual();
    }

    /// <summary>Ends the decode of one text now: it shows its final text.</summary>
    public void Finish(TextBlock target)
    {
        foreach (var play in _plays.Where(p => p.Target == target).ToList()) End(play);
    }

    /// <summary>Ends every decode now (each shows its final text).</summary>
    public void FinishAll()
    {
        foreach (var play in _plays.ToList()) End(play);
    }

    /// <summary>One animation frame: ends what is over (or no longer visible) and redraws the rest. The
    /// timer calls it; a test calls it after moving its fake clock.</summary>
    public void Tick()
    {
        var now = _clock().TotalMilliseconds;
        foreach (var play in _plays.ToList())
            if (play.Timeline.IsFinished(now - play.StartedMs) || !play.Target.IsEffectivelyVisible) End(play);
        InvalidateVisual();
    }

    private void End(Play play)
    {
        if (!_plays.Remove(play)) return;
        play.Target.Opacity = play.OwnOpacity;
        if (_plays.Count == 0) _timer?.Stop();
        InvalidateVisual();
    }

    /// <summary>Where each letter of the target's text sits, in the target's own coordinates (its padding
    /// included): the text layout's own boxes, so alignment, trimming and the font are exactly the real ones.</summary>
    private static Rect[] CellsOf(TextBlock target, string text)
    {
        var layout = target.TextLayout;
        var origin = new Point(target.Padding.Left, target.Padding.Top);
        var cells = new Rect[text.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var box = layout.HitTestTextRange(i, 1).FirstOrDefault();
            cells[i] = box.Width <= 0 && box.Height <= 0 ? default : box.Translate(origin);
        }
        return cells;
    }

    // ---- drawing -------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var now = _clock().TotalMilliseconds;
        var red = this.FindResource("House.Accent") is ISolidColorBrush accent ? accent.Color : default;
        foreach (var play in _plays) Draw(context, play, now - play.StartedMs, red);
    }

    private void Draw(DrawingContext context, Play play, double ms, Color red)
    {
        var target = play.Target;
        if (target.TransformToVisual(this) is not { } transform) return;

        var opacity = play.OwnOpacity;
        for (var v = target.GetVisualParent(); v is not null; v = v.GetVisualParent()) opacity *= v.Opacity;
        var own = target.Foreground is ISolidColorBrush solid ? solid.Color : default;
        var scale = Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
        var size = play.Size * (scale > 0 ? scale : 1);

        using var clip = context.PushClip(new Rect(target.Bounds.Size).TransformToAABB(transform));
        using var fade = context.PushOpacity(Math.Clamp(opacity, 0, 1));

        var timeline = play.Timeline;
        var head = default(Rect?);
        for (var i = 0; i < play.Text.Length; i++)
        {
            if (play.Cells[i] == default) continue; // trimmed away
            var cell = play.Cells[i].TransformToAABB(transform);
            var ch = play.Text[i].ToString();

            if (play.Order[i] < 0)
            {
                // A letter that does not decode (a space, or a digit that did not change): just there.
                if (!char.IsWhiteSpace(play.Text[i])) DrawLetter(context, ch, play.Face, size, own, cell);
                continue;
            }

            var frame = timeline.FrameAt(play.Order[i], ms);
            switch (frame.Phase)
            {
                case LetterPhase.Waiting:
                    continue;
                case LetterPhase.Noise:
                    var noise = NoiseFor(play, i, frame.NoiseSwap, cell.Width / Math.Max(scale, 0.0001), size);
                    DrawLetter(context, noise.ToString(), play.Face, size, WithAlpha(own, 0.42), cell);
                    break;
                default:
                    DrawLetter(context, ch, play.Face, size, Mix(red, own, frame.Lock), cell);
                    break;
            }
            if (play.Order[i] == timeline.HeadOrder(ms)) head = cell;
        }

        // The write head: a thin red line just after the newest letter; it fades after the last one settles.
        var gone = timeline.HeadFadeAt(ms);
        if (head is { } at && gone < 1)
            context.DrawRectangle(new ImmutableSolidColorBrush(WithAlpha(red, 1 - gone)), null,
                new Rect(at.Right + 1.5, at.Top + at.Height * 0.1, 1.5, at.Height * 0.8));
    }

    private static void DrawLetter(DrawingContext context, string letter, Typeface face, double size, Color colour, Rect cell)
    {
        var text = new FormattedText(letter, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, new ImmutableSolidColorBrush(colour));
        // Centred in the letter's own cell, so a narrower random character does not lean to one side.
        context.DrawText(text, new Point(cell.X + (cell.Width - text.Width) / 2, cell.Y));
    }

    /// <summary>A random character about as wide as letter <paramref name="i"/> (a digit for a digit); the
    /// same one for a whole 30 ms period, a new one for the next.</summary>
    private static char NoiseFor(Play play, int i, int swap, double width, double size)
    {
        var digits = char.IsDigit(play.Text[i]);
        var pool = PoolOf(play.Face, size, digits);
        var near = pool.Where(p => Math.Abs(p.W - width) <= Math.Max(1.2, width * 0.18)).ToArray();
        if (near.Length < 3) near = pool.OrderBy(p => Math.Abs(p.W - width)).Take(4).ToArray();
        var hash = HashCode.Combine(play.Seed, i, swap) & int.MaxValue;
        return near[hash % near.Length].C;
    }

    private static (char C, double W)[] PoolOf(Typeface face, double size, bool digits)
    {
        if (Pools.TryGetValue((face, size, digits), out var pool)) return pool;
        pool = (digits ? NoiseDigits : NoiseLetters)
            .Select(c => (c, new FormattedText(c.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, null).Width))
            .ToArray();
        return Pools[(face, size, digits)] = pool;
    }

    private static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(c.A * alpha, 0, 255), c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
