namespace RankMaster2.Pc.Ui.Surface;

/// <summary>The model's only source of "now". A test supplies a fake so every millisecond rule in
/// § 3.1 and § 3.2 (the 200 ms arrival guard, the 300 ms still-ring grace, the 1 s engine-wake
/// notice, the 2.5 s toast, …) runs deterministically with no real delay.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production: the real clock. Views/ constructs this; Surface/ never calls
/// <c>DateTimeOffset.UtcNow</c> directly, so every timing rule stays testable.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>A clock a test can move by hand. Not thread-safe; the model is single-threaded by
/// design (everything is marshalled onto one thread through <see cref="IUiThread"/>).</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }

    public FakeClock(DateTimeOffset? start = null) => UtcNow = start ?? DateTimeOffset.UnixEpoch;

    public void Advance(TimeSpan by) => UtcNow += by;
}
