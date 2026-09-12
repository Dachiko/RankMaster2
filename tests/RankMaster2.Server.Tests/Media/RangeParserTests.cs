using RankMaster2.Server.Media;
using Xunit;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// SERVER_SPEC.md § 12.4. Range is the part of the video endpoint a player exercises hardest and
/// the part where an off-by-one is invisible until a seek lands in the wrong place.
/// </summary>
public class RangeParserTests
{
    private const long Size = 1000;

    [Fact]
    public void NoHeader_ServesTheWholeBody()
    {
        Assert.Equal(RangeOutcome.NotRequested, RangeParser.Parse(null, Size).Outcome);
        Assert.Equal(RangeOutcome.NotRequested, RangeParser.Parse("", Size).Outcome);
        Assert.Equal(RangeOutcome.NotRequested, RangeParser.Parse("   ", Size).Outcome);
    }

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=100-199", 100, 199)]
    [InlineData("bytes=999-999", 999, 999)]
    public void ClosedRange_IsServedExactly(string header, long from, long to)
    {
        var result = RangeParser.Parse(header, Size);
        Assert.Equal(RangeOutcome.Satisfiable, result.Outcome);
        Assert.Equal(from, result.Range.From);
        Assert.Equal(to, result.Range.To);
        Assert.Equal(to - from + 1, result.Range.Length);
    }

    [Fact]
    public void ClosedRange_PastTheEnd_IsClampedRatherThanRefused()
    {
        var result = RangeParser.Parse("bytes=900-99999", Size);
        Assert.Equal(RangeOutcome.Satisfiable, result.Outcome);
        Assert.Equal(900, result.Range.From);
        Assert.Equal(999, result.Range.To);
    }

    [Fact]
    public void OpenEnded_RunsToTheEnd()
    {
        var result = RangeParser.Parse("bytes=500-", Size);
        Assert.Equal(RangeOutcome.Satisfiable, result.Outcome);
        Assert.Equal(500, result.Range.From);
        Assert.Equal(999, result.Range.To);
        Assert.Equal(500, result.Range.Length);
    }

    [Fact]
    public void Suffix_TakesTheLastNBytes()
    {
        var result = RangeParser.Parse("bytes=-500", Size);
        Assert.Equal(RangeOutcome.Satisfiable, result.Outcome);
        Assert.Equal(500, result.Range.From);
        Assert.Equal(999, result.Range.To);
        Assert.Equal(500, result.Range.Length);
    }

    [Fact]
    public void Suffix_LongerThanTheFile_IsTheWholeFile()
    {
        var result = RangeParser.Parse("bytes=-99999", Size);
        Assert.Equal(RangeOutcome.Satisfiable, result.Outcome);
        Assert.Equal(0, result.Range.From);
        Assert.Equal(999, result.Range.To);
    }

    [Fact]
    public void SuffixOfZero_NamesNothing_AndIsUnsatisfiable()
    {
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=-0", Size).Outcome);
    }

    [Fact]
    public void StartAtOrPastTheEnd_IsUnsatisfiable()
    {
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=1000-", Size).Outcome);
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=1000-1010", Size).Outcome);
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=5000-", Size).Outcome);
    }

    /// <summary>
    /// A zero-length representation has no byte positions at all, so every byte range against it
    /// is unsatisfiable — including <c>bytes=0-</c>, which reads like a request for everything.
    /// </summary>
    [Theory]
    [InlineData("bytes=0-")]
    [InlineData("bytes=0-0")]
    [InlineData("bytes=-1")]
    [InlineData("bytes=-500")]
    public void AnyRange_OnAZeroLengthFile_IsUnsatisfiable(string header)
    {
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse(header, 0).Outcome);
    }

    [Fact]
    public void ZeroLengthFile_WithNoRange_IsStillTheWholeBody()
    {
        Assert.Equal(RangeOutcome.NotRequested, RangeParser.Parse(null, 0).Outcome);
    }

    /// <summary>
    /// § 12.4: a syntactically invalid <c>Range</c> is <b>ignored</b> and the full body is served
    /// with 200. Not a 416 and not a 400 — the distinction between "cannot parse" and "parsed and
    /// cannot serve" is the whole of this table.
    /// </summary>
    [Theory]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=")]
    [InlineData("bytes=100")]
    [InlineData("bytes=200-100")]     // last < first: an invalid byte-range-spec
    [InlineData("bytes=-")]
    [InlineData("bytes=1-2,")]        // an empty member makes the whole header invalid
    [InlineData("items=0-99")]        // a range unit the server does not understand
    [InlineData("0-99")]              // no unit at all
    [InlineData("bytes=-1.5")]
    [InlineData("bytes=0x10-0x20")]
    // RFC 9110 spells the unit as the literal "bytes=", with no space around it, so a
    // header that pads it is not a Range header this server understands.
    [InlineData("bytes = 10-20")]
    public void InvalidSyntax_IsIgnored_NotRefused(string header)
    {
        Assert.Equal(RangeOutcome.NotRequested, RangeParser.Parse(header, Size).Outcome);
    }

    /// <summary>
    /// § 12.4: several ranges are served as a single 206 covering only the first. Multipart byte
    /// ranges are never produced.
    /// </summary>
    [Fact]
    public void MultipleRanges_ServeOnlyTheFirst()
    {
        var result = RangeParser.Parse("bytes=0-99,200-299,400-499", Size);
        Assert.Equal(RangeOutcome.Satisfiable, result.Outcome);
        Assert.Equal(0, result.Range.From);
        Assert.Equal(99, result.Range.To);
    }

    [Fact]
    public void MultipleRanges_FirstUnsatisfiable_IsUnsatisfiable()
    {
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=5000-,0-99", Size).Outcome);
    }

    [Fact]
    public void ContentRange_Headers_AreFormattedAsTheContractSpells()
    {
        Assert.Equal("bytes 0-99/1000", RangeParser.ContentRange(new ByteRange(0, 99), 1000));
        Assert.Equal("bytes 500-999/1000", RangeParser.ContentRange(new ByteRange(500, 999), 1000));
        Assert.Equal("bytes */1000", RangeParser.UnsatisfiedContentRange(1000));
        Assert.Equal("bytes */0", RangeParser.UnsatisfiedContentRange(0));
    }

    [Fact]
    public void SingleByteFile_CanBeRangedExactlyOnce()
    {
        var result = RangeParser.Parse("bytes=0-0", 1);
        Assert.Equal(RangeOutcome.Satisfiable, result.Outcome);
        Assert.Equal(1, result.Range.Length);
        Assert.Equal(RangeOutcome.Unsatisfiable, RangeParser.Parse("bytes=1-", 1).Outcome);
    }
}
