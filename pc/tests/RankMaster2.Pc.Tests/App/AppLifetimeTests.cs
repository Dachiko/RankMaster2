namespace RankMaster2.Pc.Tests.App;

using System.Diagnostics;
using RankMaster2.Pc.App;
using RankMaster2.Pc.Tests.App.Fakes;
using Xunit;

/// <summary>A-startup-and-shell.md § 8, tests 10 and 12.</summary>
public sealed class AppLifetimeTests
{
    /// <summary>Stands in for part E's <c>UiRoot.QuitRequested</c> (E § 2.2) — same shape (a plain
    /// event), so the wiring <see cref="Composition"/> does (subscribe, call
    /// <see cref="AppLifetime.PrepareQuitAsync"/>) is proven without needing real key routing
    /// through an Avalonia control tree.</summary>
    private sealed class FakeQuitSource
    {
        public event EventHandler? QuitRequested;
        public void RaiseQuit() => QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    [Fact]
    public async Task QuitRequested_RunsPrepareQuit()
    {
        var closeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = new FakeSessionLink
        {
            OnClose = _ =>
            {
                closeStarted.TrySetResult(true);
                return Task.CompletedTask;
            },
        };

        var logPath = Path.Combine(Path.GetTempPath(), $"rm2-lifetime-{Guid.NewGuid():N}.log");
        var lifetime = new AppLifetime(link, logPath);
        var root = new FakeQuitSource();

        var prepareQuitCalls = 0;
        root.QuitRequested += (_, _) =>
        {
            prepareQuitCalls++;
            _ = lifetime.PrepareQuitAsync();
        };

        root.RaiseQuit();

        var observed = await Task.WhenAny(closeStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(600)));
        Assert.Same(closeStarted.Task, observed);
        Assert.Equal(1, prepareQuitCalls);
        Assert.Equal(1, link.CloseCalls);

        File.Delete(logPath);
    }

    /// <summary>G-audit-remediation.md § 3.7, A21: the old blanket <c>catch { }</c> around
    /// <c>ISessionLink.CloseAsync</c> hid a genuinely failing close as readily as an expected
    /// timeout. Esc still ends the process regardless (this test calls <c>PrepareQuitAsync</c>
    /// directly, exactly as the others in this file do, so the test host survives) — the exception's
    /// type and message now land in the startup log rather than nowhere.</summary>
    [Fact]
    public async Task CloseFailure_IsWrittenToTheStartupLog()
    {
        var link = new FakeSessionLink
        {
            OnClose = _ => throw new InvalidOperationException("the link refused to close"),
        };

        var logPath = Path.Combine(Path.GetTempPath(), $"rm2-lifetime-closefail-{Guid.NewGuid():N}.log");
        var lifetime = new AppLifetime(link, logPath);

        try
        {
            await lifetime.PrepareQuitAsync();

            var text = await File.ReadAllTextAsync(logPath);
            Assert.Contains("close_failed", text);
            Assert.Contains(nameof(InvalidOperationException), text);
            Assert.Contains("the link refused to close", text);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task Esc_PrepareQuit_CapsAt500ms()
    {
        var link = new FakeSessionLink
        {
            // Never completes on its own; only cancellation (AppLifetime's own 500 ms CTS) ends it.
            OnClose = async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct),
        };

        var logPath = Path.Combine(Path.GetTempPath(), $"rm2-lifetime-cap-{Guid.NewGuid():N}.log");
        var lifetime = new AppLifetime(link, logPath);

        var sw = Stopwatch.StartNew();
        await lifetime.PrepareQuitAsync();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 600, $"PrepareQuitAsync took {sw.ElapsedMilliseconds} ms, expected < 600");

        File.Delete(logPath);
    }
}
