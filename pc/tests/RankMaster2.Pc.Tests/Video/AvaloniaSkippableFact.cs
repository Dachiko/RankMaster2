namespace RankMaster2.Pc.Tests.Video;

using System.ComponentModel;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

/// <summary>
/// <c>LibVlcTests</c> needs two things neither of Avalonia.Headless.XUnit's <c>[AvaloniaFact]</c>/
/// <c>[AvaloniaTheory]</c> and Xunit.SkippableFact's <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>
/// gives on its own: the real <see cref="VideoSurface"/> creates an Avalonia <c>WriteableBitmap</c>
/// once a frame arrives, which needs Avalonia's headless platform up and the test dispatched onto its
/// session the way <c>AvaloniaFact</c> does; and libvlc is only installed in the container
/// (<c>pc/tests/run-video-linux.sh</c>), which needs a dynamic <c>Skip.If</c> the way
/// <c>SkippableFact</c> does. Neither composes with the other by just stacking attributes — each
/// wraps test execution its own way, and Avalonia.Headless.XUnit's own test-case/runner classes
/// (<c>AvaloniaTestCase</c>, <c>AvaloniaTestCaseRunner</c>, ...) are <c>internal</c>, so they cannot be
/// subclassed from here. This file reimplements the same two things — the dispatch
/// <c>AvaloniaTestCase</c> does via the public <see cref="HeadlessUnitTestSession"/>, and the
/// message-bus interception <c>SkippableFactTestCase</c> does via the public
/// <see cref="SkippableTestMessageBus"/> — combined in one test case, entirely from the public surface
/// both packages expose (xunit's own runner base classes are extensibility points by design: every
/// member this needs is <c>protected</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("RankMaster2.Pc.Tests.Video.AvaloniaSkippableFactDiscoverer", "RankMaster2.Pc.Tests")]
public sealed class AvaloniaSkippableFactAttribute : FactAttribute
{
}

public class AvaloniaSkippableFactDiscoverer : FactDiscoverer
{
    public AvaloniaSkippableFactDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink)
    {
    }

    protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute) =>
        new AvaloniaSkippableTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), testMethod);
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("RankMaster2.Pc.Tests.Video.AvaloniaSkippableTheoryDiscoverer", "RankMaster2.Pc.Tests")]
public sealed class AvaloniaSkippableTheoryAttribute : TheoryAttribute
{
}

public class AvaloniaSkippableTheoryDiscoverer : TheoryDiscoverer
{
    public AvaloniaSkippableTheoryDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink)
    {
    }

    // Mirrors AvaloniaTheoryDiscoverer: one AvaloniaSkippableTestCase per pre-enumerated data row —
    // both of LibVlcTests' MemberData sources are static and fully enumerable at discovery time.
    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute, object[] dataRow)
    {
        yield return new AvaloniaSkippableTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), testMethod, dataRow);
    }
}

/// <summary>The exception names <see cref="SkippableTestMessageBus"/> re-interprets as a skip —
/// just <see cref="SkipException"/>, exactly what <c>[SkippableFact]</c> itself registers.</summary>
internal static class SkippableExceptionNames
{
    public static readonly string[] Value = { typeof(global::Xunit.SkipException).FullName! };
}

/// <summary>Dispatches through <see cref="HeadlessUnitTestSession"/> (same as <c>AvaloniaTestCase</c>)
/// with the message bus wrapped in <see cref="SkippableTestMessageBus"/> (same as
/// <c>SkippableFactTestCase</c>).</summary>
internal class AvaloniaSkippableTestCase : XunitTestCase
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public AvaloniaSkippableTestCase()
    {
    }

    public AvaloniaSkippableTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, ITestMethod testMethod, object[]? testMethodArguments = null)
        : base(diagnosticMessageSink, defaultMethodDisplay, TestMethodDisplayOptions.None, testMethod, testMethodArguments)
    {
    }

    public override async Task<RunSummary> RunAsync(
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        object[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Method.ToRuntimeMethod().DeclaringType?.Assembly);
        var skippableBus = new SkippableTestMessageBus(messageBus, SkippableExceptionNames.Value);

        // Same reason AvaloniaTestCase does this on Task.Run: block the xunit thread so its
        // concurrency throttle stays effective while the real work runs on the dispatch session.
        var runSummary = await Task.Run(() => AvaloniaDispatchTestCaseRunner.RunTest(
            session, this, DisplayName, SkipReason, constructorArguments, TestMethodArguments,
            skippableBus, aggregator, cancellationTokenSource)).ConfigureAwait(false);

        runSummary.Failed -= skippableBus.SkippedCount;
        runSummary.Skipped += skippableBus.SkippedCount;
        return runSummary;
    }
}

/// <summary>Reimplementation of Avalonia.Headless.XUnit's (internal) <c>AvaloniaTestCaseRunner</c> /
/// <c>AvaloniaTestRunner</c> / <c>AvaloniaTestInvoker</c> chain: only the test method invocation is
/// dispatched onto the headless session, and <c>Dispatcher.UIThread.RunJobs()</c> runs after, exactly
/// as upstream does — the difference is only which <see cref="IMessageBus"/> arrives here (the
/// skip-translating one built above).</summary>
internal sealed class AvaloniaDispatchTestCaseRunner : XunitTestCaseRunner
{
    private readonly HeadlessUnitTestSession _session;

    private AvaloniaDispatchTestCaseRunner(
        HeadlessUnitTestSession session, IXunitTestCase testCase, string displayName, string skipReason,
        object[] constructorArguments, object[] testMethodArguments, IMessageBus messageBus,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        : base(testCase, displayName, skipReason, constructorArguments, testMethodArguments, messageBus, aggregator, cancellationTokenSource)
    {
        _session = session;
    }

    public static Task<RunSummary> RunTest(
        HeadlessUnitTestSession session, IXunitTestCase testCase, string displayName, string skipReason,
        object[] constructorArguments, object[] testMethodArguments, IMessageBus messageBus,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    {
        var runner = new AvaloniaDispatchTestCaseRunner(session, testCase, displayName, skipReason,
            constructorArguments, testMethodArguments, messageBus, aggregator, cancellationTokenSource);
        return runner.RunAsync();
    }

    protected override XunitTestRunner CreateTestRunner(
        ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments,
        MethodInfo testMethod, object[] testMethodArguments, string skipReason,
        IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource) =>
        new AvaloniaDispatchTestRunner(_session, test, messageBus, testClass, constructorArguments,
            testMethod, testMethodArguments, skipReason, beforeAfterAttributes, aggregator, cancellationTokenSource);

    private sealed class AvaloniaDispatchTestRunner : XunitTestRunner
    {
        private readonly HeadlessUnitTestSession _session;

        public AvaloniaDispatchTestRunner(
            HeadlessUnitTestSession session, ITest test, IMessageBus messageBus, Type testClass,
            object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments,
            string skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes,
            ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
            : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments,
                skipReason, beforeAfterAttributes, aggregator, cancellationTokenSource)
        {
            _session = session;
        }

        protected override Task<decimal> InvokeTestMethodAsync(ExceptionAggregator aggregator) =>
            _session.Dispatch(
                () => new AvaloniaDispatchTestInvoker(Test, MessageBus, TestClass, ConstructorArguments,
                    TestMethod, TestMethodArguments, BeforeAfterAttributes, aggregator, CancellationTokenSource).RunAsync(),
                CancellationTokenSource.Token);
    }

    private sealed class AvaloniaDispatchTestInvoker : XunitTestInvoker
    {
        public AvaloniaDispatchTestInvoker(
            ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments,
            MethodInfo testMethod, object[] testMethodArguments,
            IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource)
            : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments,
                beforeAfterAttributes, aggregator, cancellationTokenSource)
        {
        }

        protected override async Task AfterTestMethodInvokedAsync()
        {
            await base.AfterTestMethodInvokedAsync();
            Aggregator.Run(() => Dispatcher.UIThread.RunJobs());
        }
    }
}
