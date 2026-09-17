// Avalonia.Headless.XUnit's [AvaloniaFact]/[AvaloniaTheory] (used by Video/LibVlcTests.cs and by
// App/'s own headless tests) need exactly this: an assembly-level AvaloniaTestApplication attribute
// naming a builder type, so the test runner knows how to stand up a headless Application before
// each test. Nothing else in the test project depends on this file directly.
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using RankMaster2.Pc.App;

[assembly: AvaloniaTestApplication(typeof(RankMaster2.Pc.Tests.TestAppBuilder))]

namespace RankMaster2.Pc.Tests;

public static class TestAppBuilder
{
    // Part E shipped its own copy of this, building a bare Application; two of them in one assembly
    // is a duplicate-attribute error. This one wins because it builds the real App, so a view test
    // sees the styles production will apply rather than an unstyled control that happens to pass.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<RankMaster2.Pc.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
