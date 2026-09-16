namespace RankMaster2.Pc.Tests.Video;

using RankMaster2.Pc.Video;
using Xunit;

/// <summary>D-video.md § 6.1 test 10: messages carry no VLC jargon, Detail carries the log lines
/// given to it.</summary>
public class FailureMapperTests
{
    private static readonly string[] VlcJargon =
    {
        "libvlc", "vlc", "demux", "fourcc", "codec module", "vout", "mrl", "es_out"
    };

    public static IEnumerable<object[]> AllSignals() =>
        Enum.GetValues<FailureSignal>().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(AllSignals))]
    public void Message_contains_no_VLC_jargon(FailureSignal signal)
    {
        var failure = FailureMapper.Map(signal, new[] { "moov atom not found" }, TimeSpan.FromSeconds(5));
        var lower = failure.Message.ToLowerInvariant();
        foreach (var jargon in VlcJargon)
            Assert.DoesNotContain(jargon, lower);
    }

    [Fact]
    public void Detail_carries_the_given_log_lines()
    {
        var lines = new[] { "line one", "line two", "no suitable decoder module for fourcc 'xxxx'" };
        var failure = FailureMapper.Map(FailureSignal.ParseFailed, lines);
        Assert.NotNull(failure.Detail);
        foreach (var line in lines)
            Assert.Contains(line, failure.Detail);
    }

    [Fact]
    public void Missing_never_carries_a_detail_even_if_lines_are_supplied()
    {
        // Step 1 of § 4.5 makes no backend call, so there is nothing VLC said about this file.
        var failure = FailureMapper.Map(FailureSignal.FileMissing, new[] { "should be ignored" });
        Assert.Null(failure.Detail);
    }

    [Theory]
    [InlineData(FailureSignal.FileMissing, VideoFailureKind.Missing)]
    [InlineData(FailureSignal.ParseFailed, VideoFailureKind.Unreadable)]
    [InlineData(FailureSignal.ParseTimedOut, VideoFailureKind.Unreadable)]
    [InlineData(FailureSignal.NoVideoTrack, VideoFailureKind.NoVideoTrack)]
    [InlineData(FailureSignal.PlayReturnedFalse, VideoFailureKind.DecodeFailed)]
    [InlineData(FailureSignal.EncounteredErrorBeforeFirstFrame, VideoFailureKind.DecodeFailed)]
    [InlineData(FailureSignal.EncounteredErrorWhilePlaying, VideoFailureKind.DecodeFailed)]
    [InlineData(FailureSignal.EndReachedBeforeFirstFrame, VideoFailureKind.DecodeFailed)]
    [InlineData(FailureSignal.NoFrameWithinTimeout, VideoFailureKind.NoFrameInTime)]
    public void Signal_maps_to_the_right_kind(FailureSignal signal, VideoFailureKind expected)
    {
        Assert.Equal(expected, FailureMapper.Map(signal).Kind);
    }

    [Fact]
    public void EndReached_before_first_frame_names_the_reason()
    {
        var failure = FailureMapper.Map(FailureSignal.EndReachedBeforeFirstFrame);
        Assert.Contains("ends before its first frame", failure.Message);
    }

    [Fact]
    public void NoFrameWithinTimeout_names_the_timeout()
    {
        var failure = FailureMapper.Map(FailureSignal.NoFrameWithinTimeout, timeout: TimeSpan.FromSeconds(5));
        Assert.Contains("5", failure.Message);
    }
}
