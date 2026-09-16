using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestCaseOrderer("RankMaster2.Pc.Link.Tests.Harness.AlphabeticalTestCaseOrderer", "RankMaster2.Pc.Link.Tests")]

namespace RankMaster2.Pc.Link.Tests.Harness;

/// <summary>
/// Runs each test class's <c>[Fact]</c>s in method-name order (W1, W2, … C1, C2, … N1, N2, …) rather
/// than xunit's undefined default. Every test in this suite is written to manufacture its own
/// preconditions rather than lean on another test's leftovers (§ 6.2's <c>RealServer</c> collection
/// fixture is shared, not reset between tests), so this is a debugging convenience — it makes a
/// failure's position in the run match the plan's own numbering — not a correctness dependency.
/// </summary>
public sealed class AlphabeticalTestCaseOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(tc => tc.TestMethod.Method.Name, StringComparer.Ordinal);
}
