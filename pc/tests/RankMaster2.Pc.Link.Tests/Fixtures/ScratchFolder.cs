using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace RankMaster2.Pc.Link.Tests.Fixtures;

/// <summary>
/// A temp folder of real PNGs the server will accept for ranking, plus a reader for
/// <c>rankmaster_db.json</c> so a test can check what actually landed on disk. The link's tests need
/// a folder that opens (two or more media files) and nothing more.
/// <para/>
/// The encoder is lifted from <c>src/rm2ctl/ScratchLibrary.cs</c>: self-contained (RFC 2083 plus
/// <see cref="DeflateStream"/>), not pulled from an imaging library, because a diagnostic/test fixture
/// that drags in an image stack has more ways to fail than the thing it exercises.
/// </summary>
public sealed class ScratchFolder : IDisposable
{
    public string Path { get; }
    private readonly List<string> _names = [];

    public ScratchFolder(int count = 6)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rm2-link-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);

        var baseNames = new[]
        {
            "alpha.png", "bravo.png", "charlie.png", "delta.png", "echo.png", "foxtrot.png",
            "golf.png", "hotel.png", "india.png", "juliet.png",
        };

        for (var i = 0; i < count; i++)
        {
            var name = i < baseNames.Length ? baseNames[i] : $"item {i + 1}.png";
            var shade = i;
            File.WriteAllBytes(System.IO.Path.Combine(Path, name),
                EncodePng(120 + i * 20, 90 + i * 10, (x, y) => (
                    (byte)((x * 3 + shade * 40) % 256),
                    (byte)((y * 5 + shade * 25) % 256),
                    (byte)((x + y + shade * 60) % 256))));
            _names.Add(name);
        }
    }

    public IReadOnlyList<string> Names => _names;

    public string FullPathOf(string name) => System.IO.Path.Combine(Path, name);

    public bool Exists(string name) => File.Exists(FullPathOf(name));
    public bool InDiscarded(string name) => File.Exists(System.IO.Path.Combine(Path, "discarded", name));
    public bool InSpecial(string name) => File.Exists(System.IO.Path.Combine(Path, "special 1", name));

    public void DeleteFile(string name) => File.Delete(FullPathOf(name));

    /// <summary>Parses <c>rankmaster_db.json</c> in this folder. Returns null if it does not exist
    /// (a session that has never saved) or does not parse.
    /// <para/>
    /// Shape (<c>RankMaster2.Catalog.RankingDatabaseDto</c>): <c>{"version","lastUpdated","images":
    /// {"&lt;filename&gt;": {"filename","rating":{"mu","sigma"},"matches","impressions","lastPlayed"}}}</c>
    /// — <c>images</c> is a JSON <b>object</b> keyed by filename, not an array.</summary>
    public JsonDocument? Db()
    {
        var path = System.IO.Path.Combine(Path, "rankmaster_db.json");
        if (!File.Exists(path)) return null;
        try { return JsonDocument.Parse(File.ReadAllBytes(path)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Sum of <c>matches</c> across every record in the database (0 if there is none yet).</summary>
    public int TotalMatches()
    {
        using var db = Db();
        if (db is null) return 0;
        var total = 0;
        foreach (var record in db.RootElement.GetProperty("images").EnumerateObject())
            if (record.Value.TryGetProperty("matches", out var m) && m.TryGetInt32(out var value))
                total += value;
        return total;
    }

    /// <summary><c>matches</c> for one file, or null if it has no record yet.</summary>
    public int? MatchesOf(string name)
    {
        using var db = Db();
        if (db is null) return null;
        if (!db.RootElement.GetProperty("images").TryGetProperty(name, out var record)) return null;
        return record.TryGetProperty("matches", out var m) && m.TryGetInt32(out var value) ? value : null;
    }

    /// <summary><c>impressions</c> for one file, or null if it has no record yet.</summary>
    public int? ImpressionsOf(string name)
    {
        using var db = Db();
        if (db is null) return null;
        if (!db.RootElement.GetProperty("images").TryGetProperty(name, out var record)) return null;
        return record.TryGetProperty("impressions", out var m) && m.TryGetInt32(out var value) ? value : null;
    }

    public bool HasRecord(string name)
    {
        using var db = Db();
        return db is not null && db.RootElement.GetProperty("images").TryGetProperty(name, out _);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- a small PNG encoder, lifted from rm2ctl/ScratchLibrary.cs ------------------------------

    private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    private static byte[] EncodePng(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(output, "IHDR", header);

        var raw = new byte[height * (1 + width * 3)];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0;
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                raw[at++] = r;
                raw[at++] = g;
                raw[at++] = b;
            }
        }

        Chunk(output, "IDAT", Zlib(raw));
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var wrapped = new MemoryStream();
        wrapped.WriteByte(0x78);
        wrapped.WriteByte(0x01);
        using (var deflate = new DeflateStream(wrapped, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data, 0, data.Length);

        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (b << 16) | a);
        wrapped.Write(adler);
        return wrapped.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        output.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var c = 0xFFFFFFFFu;
        foreach (var value in first) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        foreach (var value in second) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
