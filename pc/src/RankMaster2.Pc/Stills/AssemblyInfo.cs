using System.Runtime.CompilerServices;

// StillSourceTests exercises ordering/eviction/memory against the internal FakeDecoder seam
// (IStillDecoder) and StillFrameLeaseTests exercises StillFrame's ref-count directly (plan
// section 4: "StillDecoder is behind a small internal interface IStillDecoder ... so StillSource
// can be tested ... with FakeDecoder"). Both are internal by design -- E only ever sees
// IStillSource -- so the test project is named a friend assembly instead of widening the public
// surface just for tests.
[assembly: InternalsVisibleTo("RankMaster2.Pc.Stills.Tests")]

// E's test project needs the same seam for the same reason, one layer up: AUDIT2.md section 1.1 is
// about a frame being FREED under a pane that is still showing it, and no test of the ranking
// surface can prove anything about that while its still source is a fake that never frees. Driving
// the real StillSource -- real wanted set, real eviction, real reference counting, real
// DecodeBudget buffers -- through this seam leaves only the Skia call itself faked. (It has to be
// faked there: RankMaster2.Pc.Tests loads Avalonia, which drags in libSkiaSharp 2.88 on Linux,
// which SkiaSharp 3.119's managed assembly refuses to bind to. Windows takes the 3.119 native from
// SkiaSharp.NativeAssets.Win32 and has no such clash.)
[assembly: InternalsVisibleTo("RankMaster2.Pc.Tests")]
