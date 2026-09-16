using System.Runtime.CompilerServices;

// StillSourceTests exercises ordering/eviction/memory against the internal FakeDecoder seam
// (IStillDecoder) and StillFrameLeaseTests exercises StillFrame's ref-count directly (plan
// section 4: "StillDecoder is behind a small internal interface IStillDecoder ... so StillSource
// can be tested ... with FakeDecoder"). Both are internal by design -- E only ever sees
// IStillSource -- so the test project is named a friend assembly instead of widening the public
// surface just for tests.
[assembly: InternalsVisibleTo("RankMaster2.Pc.Stills.Tests")]
