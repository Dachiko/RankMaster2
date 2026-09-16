using System.Runtime.CompilerServices;

// § 3: Link/'s test project compiles these sources directly via <Compile Include>, so this is
// currently belt-and-suspenders rather than load-bearing — but it is what the plan names as "the
// only exception" to everything outside the four seam files being internal, so it is kept in case
// Link/ ever becomes its own referenced assembly.
[assembly: InternalsVisibleTo("RankMaster2.Pc.Link.Tests")]
