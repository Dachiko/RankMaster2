namespace RankMaster2.Pc.Tests.App;

using System.Diagnostics;
using RankMaster2.Pc.App;
using Xunit;

/// <summary>
/// A-startup-and-shell.md § 8, test 13. "No I/O" cannot be proven by mocking the filesystem without
/// changing every constructor's signature, so this is the pragmatic half the plan's own § 7.3 note
/// allows for: <c>LibVlcLayout.NativeDir</c> does one or two <c>File.Exists</c> stat calls (locating
/// the libvlc directory, not reading it), which is the one known exception to "no I/O" in
/// <see cref="Composition.Build"/> — cheap enough (microseconds) that it does not change the
/// startup budget, and flagged here rather than silently ignored. What this test actually proves:
/// <see cref="Composition.Build"/> runs, constructs every part, and returns well under the time a
/// network call or a native load would cost — the two things § 7.3 actually cares about excluding.
/// </summary>
public sealed class CompositionTests
{
    [Fact]
    public void Composition_Build_IsFastAndLoadsNoVideoEngine()
    {
        // See ShellTests.FirstFrame_LoadsNoVideoEngine's comment: this assembly also carries
        // Video/LibVlcTests.cs, whose LibVlcProbe.Available loads LibVLCSharp on any platform the
        // first time any LibVlc-category test runs, regardless of test order. That is a property of
        // this shared test process, not of Composition.Build — skip the load-check half rather than
        // false-fail when it has already happened; the timing half is unaffected either way.
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => (a.GetName().Name ?? "").StartsWith("LibVLCSharp", StringComparison.Ordinal));

        var sw = Stopwatch.StartNew();
        var services = Composition.Build();
        sw.Stop();

        Assert.NotNull(services.Link);
        Assert.NotNull(services.Stills);
        Assert.NotNull(services.Video);
        Assert.NotNull(services.Root);

        // A network call (ConnectAsync) or a native load (VideoEngine.WarmUpAsync) would cost tens
        // of milliseconds at least; stat-ing a couple of paths and allocating plain objects does
        // not. 200 ms is a generous ceiling, not a tight budget.
        Assert.True(sw.ElapsedMilliseconds < 200, $"Composition.Build took {sw.ElapsedMilliseconds} ms");

        if (!alreadyLoaded)
        {
            var loadedVlc = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => (a.GetName().Name ?? "").StartsWith("LibVLCSharp", StringComparison.Ordinal));
            Assert.False(loadedVlc, "Composition.Build must not load LibVLCSharp");
        }
    }
}
