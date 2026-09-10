using System.Buffers.Binary;
using System.Reflection;
using System.Text;

namespace ContinuityBridge.Qa.Protocol;

public sealed record QaFixture(string Kind, string MimeType, byte[] Bytes);

public static class FixtureCatalog
{
    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(new[]
    { "sentinel-v1", "unicode-v1", "long-text-v1", "alpha-png-v1", "jpeg-v1", "png-20000000-v1", "png-20000001-v1", "bitmap-v1", "file-drop-v1" });

    public static QaFixture Get(string id) => id switch
    {
        "sentinel-v1" => Text("CB P5/P6 synthetic clipboard sentinel"),
        "unicode-v1" => Text("  合成 fixture\r\n🙂 e\u0301\t 尾部空格  "),
        "long-text-v1" => Text(string.Concat(Enumerable.Repeat("合成 🧪 e\u0301\r\n\"\\\t 尾部  \r\n", 12000))),
        "alpha-png-v1" or "bitmap-v1" => new("image", "image/png", Png(null)),
        "jpeg-v1" => Jpeg(),
        "png-20000000-v1" => new("image", "image/png", Png(20_000_000)),
        "png-20000001-v1" => new("image", "image/png", Png(20_000_001)),
        "file-drop-v1" => new("file", "application/octet-stream", "CB synthetic file; never upload"u8.ToArray()),
        _ => throw new ArgumentException("unknown_fixture", nameof(id))
    };

    private static QaFixture Text(string value) => new("text", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(value));
    private static QaFixture Jpeg()
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("ContinuityBridge.Qa.Protocol.Fixtures.synthetic.jpg")!;
        using var output = new MemoryStream(); input.CopyTo(output); return new("image", "image/jpeg", output.ToArray());
    }

    // Deterministic RFC1950/1951 stored blocks: no dependency on zlib/library versions.
    // Large fixtures are real 2500x1999 RGBA scanlines with a legal ancillary chunk.
    private static byte[] Png(int? target)
    {
        int width = target.HasValue ? 2500 : 3, height = target.HasValue ? 1999 : 2;
        byte[] rows = new byte[checked((width * 4 + 1) * height)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int p = y * (width * 4 + 1) + 1 + x * 4;
            rows[p] = (byte)(x % 256); rows[p + 1] = (byte)((x * 7 + y * 19) % 256);
            rows[p + 2] = 128; rows[p + 3] = (byte)((x + y) % 4 * 85);
        }
        using var compressed = new MemoryStream(); compressed.Write(new byte[] { 0x78, 0x01 });
        for (int offset = 0; offset < rows.Length;)
        {
            int count = Math.Min(65535, rows.Length - offset);
            compressed.WriteByte(offset + count == rows.Length ? (byte)1 : (byte)0);
            compressed.WriteByte((byte)count); compressed.WriteByte((byte)(count >> 8));
            compressed.WriteByte((byte)~count); compressed.WriteByte((byte)(~count >> 8));
            compressed.Write(rows, offset, count); offset += count;
        }
        uint a = 1, b = 0;
        foreach (byte value in rows) { a = (a + value) % 65521; b = (b + a) % 65521; }
        byte[] adler = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(adler, b << 16 | a); compressed.Write(adler);
        using var png = new MemoryStream(); png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk(png, "IHDR"u8, header); Chunk(png, "IDAT"u8, compressed.ToArray());
        if (target.HasValue) Chunk(png, "npAD"u8, new byte[checked(target.Value - (int)png.Length - 24)]);
        Chunk(png, "IEND"u8, []); return png.ToArray();
    }

    private static void Chunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); stream.Write(number);
        stream.Write(type); stream.Write(data); uint crc = uint.MaxValue;
        foreach (byte value in type) crc = Crc(crc, value);
        foreach (byte value in data) crc = Crc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ uint.MaxValue); stream.Write(number);
    }
    private static uint Crc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}
