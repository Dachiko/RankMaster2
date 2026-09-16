using Xunit;

// xunit parallelises test classes across threads by default. The tests that touch a real Avalonia
// WriteableBitmap (StateMachineTests, LifecycleTests, LibVlcTests) go through IUiThread.Post, and the
// test-only SyncUiThread runs that action inline on whatever thread called it rather than marshaling
// to a single UI thread — that is exactly right for driving the state machine synchronously (D-video.md
// § 6.1: "a synchronous IUiThread ... drive every transition without LibVLC"), but it means two such
// tests running concurrently can call into Skia's bitmap factory from two different threads at once,
// which is not safe and was observed to abort the test host ("free(): invalid pointer") in this
// project's own verification run. Production never has this hazard: part A's real IUiThread always
// marshals to Avalonia's single dispatcher thread. Disabling parallelisation here is a test-harness
// fix, not a Video/ production concern.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
