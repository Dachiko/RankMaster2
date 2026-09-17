namespace RankMaster2.Pc.Tests.App;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using RankMaster2.Pc.App;
using RankMaster2.Pc.App.Seams;
using RankMaster2.Pc.Tests.App.Fakes;
using Xunit;

/// <summary>A-startup-and-shell.md § 8, tests 1 and 2.</summary>
public sealed class ShellTests
{
    [AvaloniaFact]
    public void Shell_IsFullScreenBlackBorderless()
    {
        var placeholder = new PlaceholderRoot("2.0.0-test");
        var services = FakeComposition.Build(root: placeholder);
        var window = new MainWindow(services);

        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.Equal(SystemDecorations.None, window.SystemDecorations);
        Assert.False(window.CanResize);
        Assert.Equal(Brushes.Black, window.Background);
        Assert.Same(placeholder, window.Content);
    }

    [AvaloniaFact]
    public async Task FirstFrame_LoadsNoVideoEngine()
    {
        // Meaningful only when nothing earlier in this same test process already loaded
        // LibVLCSharp — xunit runs every test in this assembly in one process, and
        // Video/LibVlcTests.cs's own LibVlcProbe.Available (evaluated once, lazily, the first time
        // any LibVlc-category test asks) loads the managed LibVLCSharp assembly on any platform,
        // whether or not the native library is present, purely to attempt Core.Initialize. That is
        // a property of this test *process* having run other tests first, not of the production
        // startup path this test exists to prove (§ 8 test 2's own sibling, D's test 9, documents
        // exactly the same fragility: "reliable run alone ... best-effort as part of the full run").
        // Skip rather than false-fail when that has already happened.
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => (a.GetName().Name ?? "").StartsWith("LibVLCSharp", StringComparison.Ordinal));
        if (alreadyLoaded)
            return;

        StartupClock.ResetForTests();
        StartupClock.Start("2.0.0-test");

        var services = FakeComposition.Build();
        var window = new MainWindow(services);
        window.Show();

        // Give the headless dispatcher every chance to run the Opened handler and its
        // RequestAnimationFrame callback (§ 3.1's "first_frame" mark).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        // The invariant that matters regardless of whether the headless compositor ever fires a
        // frame: nothing on this path has caused LibVLCSharp to load.
        var loadedVlc = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => (a.GetName().Name ?? "").StartsWith("LibVLCSharp", StringComparison.Ordinal));
        Assert.False(loadedVlc, "LibVLCSharp must not be loaded before first_frame");

        var logPath = Path.Combine(Path.GetTempPath(), $"rm2-shell-{Guid.NewGuid():N}.log");
        try
        {
            StartupClock.Flush(logPath);
            var line = File.ReadAllLines(logPath).Last();
            var parsed = StartupClock.ParseLine(line);
            Assert.NotNull(parsed);
            Assert.False(parsed!.Marks.ContainsKey("vlc_wake_begin"));

            if (parsed.Marks.ContainsKey("first_frame"))
                Assert.False(parsed.Marks.ContainsKey("vlc_loaded_early"));
        }
        finally
        {
            File.Delete(logPath);
        }
    }
}
