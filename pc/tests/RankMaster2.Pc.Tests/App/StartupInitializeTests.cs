namespace RankMaster2.Pc.Tests.App;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using RankMaster2.Pc.App;
using RankMaster2.Pc.Tests.App.Fakes;
using Xunit;

/// <summary>
/// AUDIT2.md § 3.1: <c>MainWindow.OnOpened</c> used to call <c>_services.Link.ConnectAsync()</c>
/// directly, discarding the result, instead of <c>CompositionResult.InitializeAsync</c> (bound to
/// <c>RankCoordinator.InitializeAsync</c> in the real <see cref="Composition.Build"/>) — the only
/// place that loads the last folder for "Resume" and the only place that raises <c>Changed</c> once
/// connecting finishes. Nothing called it, in production or in a test: <c>StartModelTests</c> tests
/// <c>StatusLineFor</c> in isolation and <c>UiRootTests</c> builds its own coordinator without going
/// through launch. This locks the wiring in at the exact seam that was wrong — the shell calls
/// whatever <see cref="CompositionResult.InitializeAsync"/> is bound to, not <c>Link.ConnectAsync</c>
/// directly — without reaching into <c>RankCoordinator</c> itself, which is Ui/Surface's file.
/// </summary>
public sealed class StartupInitializeTests
{
    [AvaloniaFact]
    public async Task OnOpened_CallsCompositionInitializeAsync()
    {
        var initializeCalls = 0;
        Func<CancellationToken, Task> initializeAsync = _ =>
        {
            initializeCalls++;
            return Task.CompletedTask;
        };

        var services = FakeComposition.Build(initializeAsync: initializeAsync);
        var window = new MainWindow(services);
        window.Show();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && initializeCalls == 0)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal(1, initializeCalls);
    }

    /// <summary>The old, broken wiring called <see cref="ISessionLink.ConnectAsync"/> straight off
    /// the link and nothing else. A fixed <see cref="MainWindow"/> must route through whatever
    /// <c>CompositionResult.InitializeAsync</c> says to do, even when that is not simply
    /// <c>Link.ConnectAsync</c> — proven here by making the two diverge and asserting only the one
    /// the record names actually ran.</summary>
    [AvaloniaFact]
    public async Task OnOpened_DoesNotBypassInitializeAsyncByCallingLinkConnectDirectly()
    {
        var linkConnectCalls = 0;
        var initializeCalls = 0;

        var link = new FakeSessionLink
        {
            OnConnect = _ =>
            {
                linkConnectCalls++;
                return Task.FromResult<RankMaster2.Pc.Link.ConnectResult>(
                    new RankMaster2.Pc.Link.ConnectResult.Connected("https://127.0.0.1", false, false));
            },
        };

        Func<CancellationToken, Task> initializeAsync = _ =>
        {
            initializeCalls++;
            return Task.CompletedTask; // deliberately does NOT call link.ConnectAsync
        };

        var services = FakeComposition.Build(link: link, initializeAsync: initializeAsync);
        var window = new MainWindow(services);
        window.Show();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && initializeCalls == 0)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal(1, initializeCalls);
        Assert.Equal(0, linkConnectCalls);
    }
}
