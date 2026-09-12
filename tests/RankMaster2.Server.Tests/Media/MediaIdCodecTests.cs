using RankMaster2.Server.Media;
using Xunit;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// SERVER_SPEC.md § 11.1: an id is a filename in one percent-encoded path segment, decoded exactly
/// once as UTF-8, with no normalisation and no case folding.
/// </summary>
public class MediaIdCodecTests
{
    [Theory]
    [InlineData("DSC_0123.jpg", "DSC_0123.jpg")]
    [InlineData("beach%20day%20%232.jpg", "beach day #2.jpg")]
    [InlineData("%C3%84rger.jpg", "Ärger.jpg")]
    [InlineData("caf%C3%A9%20%E2%80%94%20%E2%84%967.png", "café — №7.png")]
    [InlineData("sunrise%20%F0%9F%8C%85.png", "sunrise 🌅.png")]
    public void WellFormedSegments_DecodeToTheFilename(string segment, string expected)
    {
        var result = MediaIdCodec.Decode(segment);
        Assert.True(result.Ok, result.Reason);
        Assert.Equal(expected, result.Id);
    }

    /// <summary>
    /// § 11.1.1: "A space MUST be <c>%20</c>. <c>+</c> MUST NOT be used for a space — this is a
    /// path segment, not a query." So a '+' stays a '+', and a file actually named with one still
    /// resolves.
    /// </summary>
    [Fact]
    public void Plus_IsALiteralPlus_NotASpace()
    {
        var result = MediaIdCodec.Decode("a+b.jpg");
        Assert.True(result.Ok);
        Assert.Equal("a+b.jpg", result.Id);
    }

    /// <summary>
    /// § 11.1.3: no Unicode normalisation. Composed and decomposed Ä are different filenames on
    /// Linux, so they must stay different ids.
    /// </summary>
    [Fact]
    public void ComposedAndDecomposed_StayDifferentIds()
    {
        var composed = MediaIdCodec.Decode("%C3%84.jpg");        // U+00C4
        var decomposed = MediaIdCodec.Decode("A%CC%88.jpg");     // A + U+0308

        Assert.True(composed.Ok);
        Assert.True(decomposed.Ok);
        Assert.NotEqual(composed.Id, decomposed.Id);
        Assert.Equal("\u00C4.jpg", composed.Id);
        Assert.Equal("A\u0308.jpg", decomposed.Id);
    }

    /// <summary>§ 11.1.3: no case folding either. The spelling that arrives is the spelling read.</summary>
    [Fact]
    public void CaseIsPreservedExactly()
    {
        Assert.Equal("Sierra.JPG", MediaIdCodec.Decode("Sierra.JPG").Id);
    }

    /// <summary>
    /// § 11.1.5: <c>%2F</c> and <c>%5C</c> decode to separators and must be rejected. This is the
    /// check that only survives if the raw segment is decoded here rather than taken pre-decoded
    /// from the routing layer.
    /// </summary>
    [Theory]
    [InlineData("..%2Fsecret.jpg")]
    [InlineData("..%2fsecret.jpg")]
    [InlineData("..%5Csecret.jpg")]
    [InlineData("sub%2Ffile.jpg")]
    [InlineData("%2Fetc%2Fpasswd")]
    public void EncodedSeparators_AreAnEscapeAttempt(string segment)
    {
        var result = MediaIdCodec.Decode(segment);
        Assert.Equal(MediaIdFault.Escapes, result.Fault);
        Assert.Equal("separator", result.Reason);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public void DotSegments_AreAnEscapeAttempt(string segment)
    {
        Assert.Equal(MediaIdFault.Escapes, MediaIdCodec.Decode(segment).Fault);
    }

    [Fact]
    public void RootedPaths_AreAnEscapeAttempt()
    {
        Assert.Equal(MediaIdFault.Escapes, MediaIdCodec.Validate("/etc/passwd").Fault);
        Assert.Equal(MediaIdFault.Escapes, MediaIdCodec.Validate("C:\\windows\\win.ini").Fault);
    }

    /// <summary>§ 11.1.2: a byte sequence that is not valid UTF-8 is a 400, with reason "not-utf8".</summary>
    [Theory]
    [InlineData("%FF%FE.jpg")]
    [InlineData("%C3.jpg")]        // a lead byte with no continuation
    [InlineData("%80%80.jpg")]     // continuation bytes with no lead
    [InlineData("%ED%A0%80.jpg")]  // an unpaired surrogate, which UTF-8 forbids
    public void InvalidUtf8_IsRefusedRatherThanRepaired(string segment)
    {
        var result = MediaIdCodec.Decode(segment);
        Assert.Equal(MediaIdFault.Invalid, result.Fault);
        Assert.Equal("not-utf8", result.Reason);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%2")]
    [InlineData("%ZZ.jpg")]
    [InlineData("a%G0.jpg")]
    public void MalformedEscapes_AreABadRequest(string segment)
    {
        var result = MediaIdCodec.Decode(segment);
        Assert.Equal(MediaIdFault.Invalid, result.Fault);
        Assert.Equal("bad-escape", result.Reason);
    }

    [Fact]
    public void ControlCharacters_AreABadRequest()
    {
        Assert.Equal("control-character", MediaIdCodec.Decode("a%00b.jpg").Reason);
        Assert.Equal("control-character", MediaIdCodec.Decode("a%0Ab.jpg").Reason);
        Assert.Equal("control-character", MediaIdCodec.Decode("a%1Fb.jpg").Reason);
    }

    [Fact]
    public void Empty_IsABadRequest()
    {
        Assert.Equal("empty", MediaIdCodec.Decode("").Reason);
    }

    /// <summary>§ 15: at most 255 UTF-16 code units.</summary>
    [Fact]
    public void OverTwoHundredAndFiftyFiveCodeUnits_IsABadRequest()
    {
        var justRight = new string('a', 251) + ".jpg";       // 255
        var oneTooMany = new string('a', 252) + ".jpg";      // 256

        Assert.True(MediaIdCodec.Decode(justRight).Ok);
        Assert.Equal("too-long", MediaIdCodec.Decode(oneTooMany).Reason);
    }

    /// <summary>
    /// The length limit counts UTF-16 code units, so an emoji costs two even though it is one
    /// character and four bytes.
    /// </summary>
    [Fact]
    public void LengthIsCountedInUtf16CodeUnits()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F305", 128)); // 256 code units
        Assert.Equal("too-long", MediaIdCodec.Validate(emoji).Reason);
    }

    [Fact]
    public void EncodeRoundTrips_ThroughDecode()
    {
        foreach (var name in new[]
                 {
                     "DSC_0123.jpg", "beach day #2.jpg", "Ärger am Fluß — фото №7.jpg",
                     "sunrise 🌅 over the sea.png", "a+b.jpg", "100% real.jpg", "q?a&b=c.png",
                     "~tilde-and_underscore.jpg", "[brackets].jpg",
                 })
        {
            var encoded = MediaIdCodec.Encode(name);
            Assert.DoesNotContain('/', encoded);
            Assert.DoesNotContain(' ', encoded);
            Assert.DoesNotContain('+', encoded.Replace("%2B", ""));

            var decoded = MediaIdCodec.Decode(encoded);
            Assert.True(decoded.Ok, $"{name} -> {encoded}: {decoded.Reason}");
            Assert.Equal(name, decoded.Id);
        }
    }

    [Fact]
    public void Encode_UsesPercentTwentyForASpace_NeverAPlus()
    {
        Assert.Equal("beach%20day%20%232.jpg", MediaIdCodec.Encode("beach day #2.jpg"));
    }
}
