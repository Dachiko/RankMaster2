using System.Runtime.CompilerServices;

// pc/plans/A-startup-and-shell.md § 2: A's tests/RankMaster2.Pc.Tests project carries every part's
// tests (App/, Ui/, Video/) against this one assembly. A handful of Video/'s test-support hooks
// (VideoSurface.CurrentGeneration, .SetHolding, .SampleStatisticsOnWorker — used only by
// VideoEngine's own alternate-mode coordinator and by D's tests) are `internal` rather than public,
// since they are not part of the frozen IVideoSurface seam; this grants the test assembly the
// visibility it needs without widening that seam.
[assembly: InternalsVisibleTo("RankMaster2.Pc.Tests")]
