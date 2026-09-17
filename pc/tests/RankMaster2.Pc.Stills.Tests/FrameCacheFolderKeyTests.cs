using System.Runtime.InteropServices;
using RankMaster2.Pc.Stills;
using Xunit;

namespace RankMaster2.Pc.Stills.Tests;

/// <summary>
/// Regression test for the second independent audit's § 3.13 finding assigned to this package --
/// "the desktop twin of § 1.2": <see cref="FrameCache"/> used to key every entry by <c>id</c>
/// alone, so two different folders that each held a same-named file of identical length and
/// modification time -- <see cref="StillSource"/>'s own staleness key -- collided on one cache
/// entry. Proven the same shape the server's fix was proven with
/// (<c>SecondAuditFixTests.DifferentFolders_SameNameSizeAndMtime_DoNotServeEachOthersBytes</c> in
/// <c>tests/RankMaster2.Server.Tests/Media/SecondAuditFixTests.cs</c>): two folders, one filename,
/// matching length and mtime, different bytes, the wrong picture served before the fix and the
/// right one after.
/// <para/>
/// This runs at the level the audit named the bug at -- <see cref="FrameCache"/>'s key, through
/// <see cref="StillSource"/>, exactly the object the app itself uses -- rather than only against
/// <see cref="FrameCache"/> in isolation, because a unit test on the key alone cannot show a wrong
/// photograph reaching a pane the way this one does.
/// </summary>
public sealed class FrameCacheFolderKeyTests
{
    /// <summary>
    /// Stands in for the real Skia decoder (plan section 6's seam) but, instead of a fixed pixel
    /// size, stamps the decoded frame's first pixel with the first byte of the file it actually
    /// read off disk. That marker is the test's only way to tell "folder A's holiday.jpg" and
    /// "folder B's holiday.jpg" apart once both have been decoded -- the two files agree on name,
    /// length and mtime by construction, so nothing else distinguishes them.
    /// </summary>
    private sealed class MarkingDecoder : IStillDecoder
    {
        private readonly DecodeBudget _budget = new(64L * 1024 * 1024);
        private readonly object _gate = new();
        public List<string> CallLog { get; } = [];

        public DecodeResult Decode(string path, int paneW, int paneH)
        {
            lock (_gate) CallLog.Add(path);

            var marker = File.ReadAllBytes(path) is { Length: > 0 } bytes ? bytes[0] : (byte)0;
            const int size = 4;
            var buffer = _budget.Allocate((long)size * size * 4);
            var frame = new StillFrame(Path.GetFileName(path), buffer, size, size, size, size, isPartial: false);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    Marshal.WriteByte(frame.Pixels, y * frame.RowBytes + x * 4, marker);
            return DecodeResult.Ok(frame);
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(5);
        }
        return predicate();
    }

    /// <summary>Writes a file with an exact byte length and an exact mtime -- the audit's own precondition.</summary>
    private static void WriteExact(string path, byte marker, int length, DateTime mtimeUtc)
    {
        var bytes = new byte[length];
        bytes[0] = marker;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, mtimeUtc);
    }

    [Fact]
    public async Task DifferentFolders_SameNameLengthAndMtime_DoNotServeEachOthersFrame()
    {
        var folderA = Directory.CreateTempSubdirectory("rm2-framekey-a-").FullName;
        var folderB = Directory.CreateTempSubdirectory("rm2-framekey-b-").FullName;
        try
        {
            var mtime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            const int length = 32;

            // The audit's precondition, proven rather than assumed: same name, same length, same
            // mtime, different bytes.
            WriteExact(Path.Combine(folderA, "holiday.jpg"), marker: 0xAA, length, mtime);
            WriteExact(Path.Combine(folderB, "holiday.jpg"), marker: 0x55, length, mtime);
            Assert.Equal(
                new FileInfo(Path.Combine(folderA, "holiday.jpg")).Length,
                new FileInfo(Path.Combine(folderB, "holiday.jpg")).Length);
            Assert.Equal(
                File.GetLastWriteTimeUtc(Path.Combine(folderA, "holiday.jpg")),
                File.GetLastWriteTimeUtc(Path.Combine(folderB, "holiday.jpg")));

            var decoder = new MarkingDecoder();
            await using var source = new StillSource(decoder);

            // Folder A first -- ordinary use, one pair, both panes on the same file for simplicity.
            source.Show(folderA, "holiday.jpg", "holiday.jpg");
            Assert.True(
                await WaitUntil(() => source.StateOf("holiday.jpg") is StillState.Ready, TimeSpan.FromSeconds(5)),
                "folder A's holiday.jpg never became Ready");

            byte markerA;
            using (var lease = ((StillState.Ready)source.StateOf("holiday.jpg")).Lease)
                markerA = Marshal.ReadByte(lease.Frame.Pixels, 0);
            Assert.Equal(0xAA, markerA);

            // Folder B next, on the SAME StillSource -- no ReleaseAllAsync in between. This is
            // exactly the audit's point: nothing calls it on an ordinary folder switch
            // (RankCoordinator.OpenFolderAsync never does either), so whatever FrameCache still
            // holds from folder A is what folder B's Show() has to contend with.
            source.Show(folderB, "holiday.jpg", "holiday.jpg");
            Assert.True(
                await WaitUntil(() => source.StateOf("holiday.jpg") is StillState.Ready, TimeSpan.FromSeconds(5)),
                "folder B's holiday.jpg never became Ready");

            byte markerB;
            using (var lease = ((StillState.Ready)source.StateOf("holiday.jpg")).Lease)
                markerB = Marshal.ReadByte(lease.Frame.Pixels, 0);

            // Before the fix: FrameCache's key was "holiday.jpg" alone, matching (length, mtime)
            // hid the folder switch entirely, EnsureFreshLocked found a "fresh" cached frame and
            // never called back into the decoder for folder B at all -- markerB came back 0xAA,
            // folder A's picture, under folder B's name. After the fix the key is (folder, id), so
            // folder B's key was never in the cache and a fresh decode ran.
            Assert.Equal(0x55, markerB);
            Assert.Contains(Path.Combine(folderB, "holiday.jpg"), decoder.CallLog);
        }
        finally
        {
            try { Directory.Delete(folderA, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(folderB, recursive: true); } catch { /* best effort */ }
        }
    }
}
