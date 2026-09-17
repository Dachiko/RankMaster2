namespace RankMaster2.Pc.Ui.Surface;

/// <summary>Every constant in one place (plan § 2.4), so a test can run the whole gate against a
/// fake clock with no <c>Task.Delay</c>.</summary>
public static class Timings
{
    public static readonly TimeSpan CueMs = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ArrivalGuardMs = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan LateActionLineAfterMs = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan StillRingGraceMs = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan EngineWakeNoticeAfterMs = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan ToastMs = TimeSpan.FromMilliseconds(2500);
    public static readonly TimeSpan ReleaseWaitMaxMs = TimeSpan.FromMilliseconds(2000);
    public static readonly TimeSpan CursorHideAfterMs = TimeSpan.FromMilliseconds(3000);
    public static readonly TimeSpan StripBallPopMs = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan ProgressBarAnimMs = TimeSpan.FromMilliseconds(200);
}
