using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RankMaster2.Pc.Ui.Views;

/// <summary>
/// Plan H § 5 S3's fade (DESIGN.md § 4: overlays and swapped content "fade in (and fade out when they
/// close)", ~150 ms, ease-out) for one control that comes and goes: the ink card and the keys page.
/// <see cref="Show"/> and <see cref="Hide"/> are the only calls; <see cref="IsShown"/> is the state the
/// owner asked for, which is what code and tests should read -- the control's own
/// <c>IsVisible</c> lags behind a hide by the fade time. A hidden control takes no pointer input from the
/// moment it starts to fade, and nothing waits on the animation.
/// </summary>
public sealed class FadeHost
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    private readonly Control _control;
    private IDisposable? _pendingHide;

    public FadeHost(Control control)
    {
        _control = control;
        _control.Transitions =
        [
            new DoubleTransition { Property = Control.OpacityProperty, Duration = Duration, Easing = new CubicEaseOut() },
        ];
    }

    /// <summary>What was last asked for. The control starts hidden.</summary>
    public bool IsShown { get; private set; }

    public void Show()
    {
        if (IsShown) return;
        IsShown = true;
        _pendingHide?.Dispose();
        _pendingHide = null;
        _control.IsHitTestVisible = true;
        if (!_control.IsVisible)
        {
            _control.Opacity = 0;
            _control.IsVisible = true;
        }
        _control.Opacity = 1;
    }

    public void Hide()
    {
        if (!IsShown) return;
        IsShown = false;
        _control.IsHitTestVisible = false;
        _control.Opacity = 0;
        _pendingHide?.Dispose();
        _pendingHide = DispatcherTimer.RunOnce(() =>
        {
            _pendingHide = null;
            if (!IsShown) _control.IsVisible = false;
        }, Duration);
    }
}

/// <summary>
/// What <see cref="UiRoot"/> hands the start screen and the rename card for plan H § 5 S3: the
/// <see cref="DecodeLayer"/> their texts decode in, and the <see cref="UserActivity"/> that says whether a
/// change is the owner's. "Nothing moves by itself": <see cref="Change"/> decodes a text only when it was
/// really changed (not the first time it is set, not for the same text again) and only when the change is
/// the owner's; a background refresh lands still.
/// </summary>
public sealed class HouseMotion
{
    public HouseMotion(DecodeLayer layer, UserActivity activity)
    {
        Layer = layer;
        Activity = activity;
    }

    public DecodeLayer Layer { get; }
    public UserActivity Activity { get; }

    /// <summary>Sets <paramref name="target"/>'s text and, when it differs from what was there and
    /// <paramref name="userCaused"/>, decodes it in. <paramref name="onlyChangedLetters"/> is for a figure
    /// (the percent): unchanged digits stay still, only the ones that changed roll.</summary>
    public void Change(TextBlock target, string text, bool userCaused, bool onlyChangedLetters = false)
    {
        var before = target.Text;
        if (before == text) return;
        target.Text = text;
        if (!userCaused || text.Length == 0) return;
        Layer.In(target, onlyChangedLetters && !string.IsNullOrEmpty(before) ? DecodeTimeline.ChangedMask(before, text) : null);
    }
}
