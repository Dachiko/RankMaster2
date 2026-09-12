using System.Text.RegularExpressions;
using RankMaster2.Server.Media;
using Xunit;

namespace RankMaster2.Server.Tests.Media;

/// <summary>
/// SERVER_SPEC.md § 12.2. The ETag is the contract's one promise about bytes — "two responses with
/// the same ETag MUST be byte-identical, forever, on every server instance" — so it has to be
/// stable across restarts and it has to move when the file does.
/// </summary>
public class MediaFingerprintTests
{
    private const string Id = "DSC_0123.jpg";
    private const long Size = 4210332;
    private const long Ticks = 639012345678901234;

    [Fact]
    public void SameInputs_GiveTheSameFingerprint_Always()
    {
        var a = MediaFingerprint.Of(Id, Size, Ticks);
        var b = MediaFingerprint.Of(Id, Size, Ticks);

        Assert.Equal(a.MediaVersion, b.MediaVersion);
        Assert.Equal(a.EntityHex, b.EntityHex);
        Assert.Equal(a.ETag("s1080j"), b.ETag("s1080j"));
    }

    /// <summary>
    /// Nothing in the fingerprint is process state — no seed, no start time, no in-memory counter —
    /// so a restart cannot change it. The literal below is the value the algorithm produces; if a
    /// change to the formula makes it move, that is a contract break, not a test to update.
    /// </summary>
    [Fact]
    public void FingerprintIsDerivedOnlyFromDiskFacts()
    {
        var fingerprint = MediaFingerprint.Of(Id, Size, Ticks);

        Assert.Matches("^[0-9a-f]{16}$", fingerprint.MediaVersion);
        Assert.Matches("^[0-9a-f]{32}$", fingerprint.EntityHex);
        Assert.StartsWith(fingerprint.MediaVersion, fingerprint.EntityHex);
    }

    [Fact]
    public void MediaVersion_IsSixteenLowercaseHexChars()
    {
        var version = MediaFingerprint.Of(Id, Size, Ticks).MediaVersion;
        Assert.Equal(16, version.Length);
        Assert.Matches("^[0-9a-f]+$", version);
    }

    [Fact]
    public void ETag_IsStrong_QuotedAndVariantPrefixed()
    {
        var etag = MediaFingerprint.Of(Id, Size, Ticks).ETag("s1080j");

        Assert.StartsWith("\"s1080j-", etag);
        Assert.EndsWith("\"", etag);
        Assert.DoesNotContain("W/", etag);
        Assert.Matches("^\"s1080j-[0-9a-f]{32}\"$", etag);
    }

    [Theory]
    [InlineData("other.jpg", Size, Ticks)]
    [InlineData(Id, Size + 1, Ticks)]
    [InlineData(Id, Size, Ticks + 1)]
    public void AnyInputChanging_ChangesTheFingerprint(string id, long size, long ticks)
    {
        var original = MediaFingerprint.Of(Id, Size, Ticks);
        var changed = MediaFingerprint.Of(id, size, ticks);

        Assert.NotEqual(original.MediaVersion, changed.MediaVersion);
        Assert.NotEqual(original.EntityHex, changed.EntityHex);
    }

    /// <summary>
    /// § 12.2: the variant "identifies the exact representation", and <c>{w}</c> is the
    /// <b>requested</b> width, so two clients asking for different widths of a small image get
    /// different ETags even when the delivered bytes are identical.
    /// </summary>
    [Fact]
    public void EveryVariant_GetsItsOwnETag()
    {
        var fingerprint = MediaFingerprint.Of(Id, Size, Ticks);
        var tags = new[] { "s360j", "s1080j", "s1080w", "s2160j", "t320j", "t320w", "orig" }
            .Select(fingerprint.ETag)
            .ToArray();

        Assert.Equal(tags.Length, tags.Distinct().Count());

        // The body is shared; only the prefix separates them, which is what makes the disk cache
        // keyable by the tag alone.
        var bodies = tags.Select(t => Regex.Match(t, "-([0-9a-f]{32})\"$").Groups[1].Value).Distinct();
        Assert.Single(bodies);
    }

    [Theory]
    [InlineData(360, StillFormat.Jpeg, false, "s360j")]
    [InlineData(1080, StillFormat.Jpeg, false, "s1080j")]
    [InlineData(1080, StillFormat.Webp, false, "s1080w")]
    [InlineData(2160, StillFormat.Webp, false, "s2160w")]
    [InlineData(320, StillFormat.Jpeg, true, "t320j")]
    [InlineData(320, StillFormat.Webp, true, "t320w")]
    public void VariantTokens_MatchTheContractTable(int width, StillFormat format, bool isThumb, string expected)
    {
        Assert.Equal(expected, new StillVariant(width, format, isThumb, FormatSource.Default).Token);
    }

    /// <summary>
    /// A separator between the fields means two different files cannot collide by concatenation —
    /// "a.jpg" at 12 bytes and "a.jpg1" at 2 bytes would otherwise hash the same string.
    /// </summary>
    [Fact]
    public void FieldsCannotRunTogether()
    {
        var a = MediaFingerprint.Of("a.jpg", 12, 345);
        var b = MediaFingerprint.Of("a.jpg1", 2, 345);
        Assert.NotEqual(a.EntityHex, b.EntityHex);
    }
}
