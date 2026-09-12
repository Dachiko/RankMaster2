using System.Buffers.Binary;
using System.Text;

namespace RankMaster2.Server.Tests.Fixtures;

/// <summary>
/// A Motion-JPEG AVI writer: a RIFF container whose every frame is one baseline JPEG.
///
/// This is how the suite gets a genuinely playable video without an encoder and without a committed
/// binary. It matters that it is playable rather than plausible: the contract promises the *original
/// bytes* back (SERVER_SPEC.md § 12.4) and forbids the server from ever decoding a frame, so the
/// fixture is the only thing that can show the bytes survived a Range round trip intact.
///
/// <c>.avi</c> is in the video list in SPEC.md § Media policy and maps to <c>video/x-msvideo</c>.
/// </summary>
public static class AviWriter
{
    public static byte[] MotionJpeg(int width, int height, int frameCount, int framesPerSecond,
                                    Func<int, int, int, (byte R, byte G, byte B)> pixel)
    {
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));

        var frames = new byte[frameCount][];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var index = frame;
            frames[frame] = JpegWriter.Rgb(width, height, (x, y) => pixel(index, x, y), quality: 70);
        }

        var movi = BuildMovi(frames, out var index1);
        var hdrl = BuildHdrl(width, height, frameCount, framesPerSecond, frames.Max(f => f.Length));

        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("RIFF"));
        var riffSizeAt = output.Position;
        WriteU32(output, 0); // patched once the total is known
        output.Write(Encoding.ASCII.GetBytes("AVI "));
        output.Write(hdrl);
        output.Write(movi);
        WriteChunk(output, "idx1", index1);

        var total = output.Length;
        output.Position = riffSizeAt;
        WriteU32(output, (uint)(total - 8)); // RIFF size excludes the fourcc and the size field
        return output.ToArray();
    }

    private static byte[] BuildMovi(byte[][] frames, out byte[] index1)
    {
        using var movi = new MemoryStream();
        movi.Write(Encoding.ASCII.GetBytes("LIST"));
        var sizeAt = movi.Position;
        WriteU32(movi, 0);
        movi.Write(Encoding.ASCII.GetBytes("movi"));
        var dataStart = movi.Position; // idx1 offsets are relative to here

        using var index = new MemoryStream();
        foreach (var frame in frames)
        {
            var offset = movi.Position - dataStart + 4; // +4: offsets count from the 'movi' fourcc
            index.Write(Encoding.ASCII.GetBytes("00dc"));
            WriteU32(index, 0x10); // AVIIF_KEYFRAME — every MJPEG frame is one
            WriteU32(index, (uint)offset);
            WriteU32(index, (uint)frame.Length);
            WriteChunk(movi, "00dc", frame);
        }

        var listSize = movi.Length - (sizeAt + 4);
        movi.Position = sizeAt;
        WriteU32(movi, (uint)listSize);
        movi.Position = movi.Length;

        index1 = index.ToArray();
        return movi.ToArray();
    }

    private static byte[] BuildHdrl(int width, int height, int frameCount, int framesPerSecond, int largestFrame)
    {
        var microsecondsPerFrame = (uint)(1_000_000 / Math.Max(1, framesPerSecond));

        using var avih = new MemoryStream();
        WriteU32(avih, microsecondsPerFrame);
        WriteU32(avih, (uint)(largestFrame * framesPerSecond)); // dwMaxBytesPerSec
        WriteU32(avih, 0);                                      // dwPaddingGranularity
        WriteU32(avih, 0x10);                                   // AVIF_HASINDEX
        WriteU32(avih, (uint)frameCount);
        WriteU32(avih, 0);                                      // dwInitialFrames
        WriteU32(avih, 1);                                      // one stream
        WriteU32(avih, (uint)largestFrame);
        WriteU32(avih, (uint)width);
        WriteU32(avih, (uint)height);
        for (var i = 0; i < 4; i++) WriteU32(avih, 0);           // dwReserved

        using var strh = new MemoryStream();
        strh.Write(Encoding.ASCII.GetBytes("vids"));
        strh.Write(Encoding.ASCII.GetBytes("MJPG"));
        WriteU32(strh, 0);                                      // dwFlags
        WriteU16(strh, 0);                                      // wPriority
        WriteU16(strh, 0);                                      // wLanguage
        WriteU32(strh, 0);                                      // dwInitialFrames
        WriteU32(strh, 1);                                      // dwScale
        WriteU32(strh, (uint)framesPerSecond);                  // dwRate — rate/scale = fps
        WriteU32(strh, 0);                                      // dwStart
        WriteU32(strh, (uint)frameCount);                       // dwLength
        WriteU32(strh, (uint)largestFrame);
        WriteU32(strh, 0xFFFFFFFF);                             // dwQuality: use the default
        WriteU32(strh, 0);                                      // dwSampleSize: variable
        WriteU16(strh, 0); WriteU16(strh, 0);                   // rcFrame left, top
        WriteU16(strh, (ushort)width); WriteU16(strh, (ushort)height);

        using var strf = new MemoryStream();                    // BITMAPINFOHEADER
        WriteU32(strf, 40);
        WriteU32(strf, (uint)width);
        WriteU32(strf, (uint)height);
        WriteU16(strf, 1);                                      // biPlanes
        WriteU16(strf, 24);                                     // biBitCount
        strf.Write(Encoding.ASCII.GetBytes("MJPG"));            // biCompression
        WriteU32(strf, (uint)(width * height * 3));             // biSizeImage
        WriteU32(strf, 0); WriteU32(strf, 0);                   // pixels per metre
        WriteU32(strf, 0); WriteU32(strf, 0);                   // palette entries

        using var strl = new MemoryStream();
        WriteChunk(strl, "strh", strh.ToArray());
        WriteChunk(strl, "strf", strf.ToArray());

        using var hdrl = new MemoryStream();
        WriteChunk(hdrl, "avih", avih.ToArray());
        WriteList(hdrl, "strl", strl.ToArray());

        using var output = new MemoryStream();
        WriteList(output, "hdrl", hdrl.ToArray());
        return output.ToArray();
    }

    private static void WriteList(Stream output, string type, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes("LIST"));
        WriteU32(output, (uint)(payload.Length + 4));
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(payload);
    }

    private static void WriteChunk(Stream output, string fourCc, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes(fourCc));
        WriteU32(output, (uint)payload.Length);
        output.Write(payload);
        if (payload.Length % 2 != 0) output.WriteByte(0); // RIFF chunks are word aligned
    }

    private static void WriteU32(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteU16(Stream output, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        output.Write(buffer);
    }
}
