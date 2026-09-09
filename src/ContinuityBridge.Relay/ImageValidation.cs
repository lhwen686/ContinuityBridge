using System.Buffers.Binary;
using System.IO.Compression;

namespace ContinuityBridge.Relay;

// This is bounded server validation, not a trusted image decoder. Clients must decode
// within their own limits before writing a clipboard image (especially JPEG entropy).
public static class ImageValidation
{
    private static readonly uint[] CrcTable = MakeCrcTable();
    public static void Validate(byte[] bytes, string mime, long maxPixels, CancellationToken token)
    {
        try
        {
            if (mime == "image/png") Png(bytes, maxPixels, token);
            else Jpeg(bytes, maxPixels, token);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException or ArgumentOutOfRangeException)
        { throw new ProtocolException(400, "invalid_request"); }
    }

    private static void Require(bool condition)
    { if (!condition) throw new InvalidDataException(); }

    private static void Png(byte[] bytes, long maxPixels, CancellationToken token)
    {
        Require(bytes.Length >= 57 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        int offset = 8, width = 0, height = 0, depth = 0, color = 0, interlace = 0;
        bool header = false, palette = false, data = false, endedData = false, transparency = false;
        int paletteEntries = 0;
        int chunks = 0;
        using var compressed = new MemoryStream();
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            Require(++chunks <= 65536 && bytes.Length - offset >= 12);
            int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            Require(count <= bytes.Length - offset - 12);
            var type = bytes.AsSpan(offset + 4, 4);
            Require(type.ToArray().All(c => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z') && (type[2] & 32) == 0);
            var content = bytes.AsSpan(offset + 8, count);
            uint crc = uint.MaxValue;
            for (int i = offset + 4; i < offset + 8 + count; i++)
            {
                if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
            }
            Require((crc ^ uint.MaxValue) == BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + count, 4)));
            Require(header || type.SequenceEqual("IHDR"u8));
            if (type.SequenceEqual("IHDR"u8))
            {
                Require(!header && count == 13);
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(content));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(content[4..]));
                Require(width > 0 && height > 0 && (long)width * height <= maxPixels);
                depth = content[8]; color = content[9]; interlace = content[12];
                Require(content[10] == 0 && content[11] == 0 && interlace <= 1);
                Require(color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false });
                header = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                Require(!palette && !data && color is not (0 or 4) && count is > 0 and <= 768 && count % 3 == 0);
                if (color == 3) Require(count / 3 <= 1 << depth);
                palette = true;
                paletteEntries = count / 3;
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                Require(!transparency && !data && (color switch { 0 => count == 2, 2 => count == 6, 3 => palette && count > 0 && count <= paletteEntries, _ => false }));
                transparency = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                Require(!endedData && (color != 3 || palette));
                data = true;
                compressed.Write(content);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                Require(data && count == 0 && offset + 12 == bytes.Length);
                compressed.Position = 0;
                ValidateScanlines(compressed, width, height, depth, color, interlace, token);
                return;
            }
            else
            {
                // Reject animation and unknown critical chunks; bounded ancillary bytes
                // are retained verbatim, never decompressed as metadata.
                Require(!type.SequenceEqual("acTL"u8) && !type.SequenceEqual("fcTL"u8) && !type.SequenceEqual("fdAT"u8) && (type[0] & 32) != 0);
            }
            if (data && !type.SequenceEqual("IDAT"u8)) endedData = true;
            offset += count + 12;
        }
        throw new InvalidDataException();
    }

    private static void ValidateScanlines(MemoryStream compressed, int width, int height, int depth, int color, int interlace, CancellationToken token)
    {
        Require(compressed.Length >= 6);
        var encoded = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
        Require((encoded[0] & 15) == 8 && (encoded[0] >> 4) <= 7 && (encoded[1] & 32) == 0 && (encoded[0] * 256 + encoded[1]) % 31 == 0);
        uint expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(encoded[^4..]);
        uint adlerA = 1, adlerB = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress, true);
        int channels = color switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        (int X, int Y, int Dx, int Dy)[] passes = interlace == 0 ? [(0, 0, 1, 1)] :
            [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        byte[] buffer = new byte[16384];
        foreach (var pass in passes)
        {
            long pw = Math.Max(0, ((long)width - pass.X + pass.Dx - 1) / pass.Dx);
            long ph = Math.Max(0, ((long)height - pass.Y + pass.Dy - 1) / pass.Dy);
            if (pw == 0 || ph == 0) continue;
            long rowBytes = (pw * depth * channels + 7) / 8;
            for (long row = 0; row < ph; row++)
            {
                token.ThrowIfCancellationRequested();
                int filter = inflater.ReadByte();
                Require(filter is >= 0 and <= 4);
                adlerA = (adlerA + (uint)filter) % 65521; adlerB = (adlerB + adlerA) % 65521;
                long remaining = rowBytes;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = inflater.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    Require(count > 0);
                    for (int i = 0; i < count; i++)
                    { adlerA = (adlerA + buffer[i]) % 65521; adlerB = (adlerB + adlerA) % 65521; }
                    remaining -= count;
                }
            }
        }
        Require(inflater.ReadByte() == -1);
        // Explicit trailer verification also rejects truncated zlib streams when a
        // platform inflater reports EOF after the expected number of scanline bytes.
        Require((adlerB << 16 | adlerA) == expectedAdler);
    }

    private static void Jpeg(byte[] bytes, long maxPixels, CancellationToken token)
    {
        Require(bytes.Length >= 16 && bytes[0] == 255 && bytes[1] == 216);
        int offset = 2, markers = 0, components = 0;
        bool frame = false, scan = false, quantization = false, huffman = false;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            Require(++markers <= 65536 && bytes[offset++] == 255);
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            Require(offset < bytes.Length);
            int marker = bytes[offset++];
            if (marker == 217) { Require(frame && scan && offset == bytes.Length); return; }
            Require(marker is not (0 or 216) && marker is not (>= 208 and <= 215) && bytes.Length - offset >= 2);
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            Require(length >= 2 && length <= bytes.Length - offset);
            var payload = bytes.AsSpan(offset + 2, length - 2);
            if (marker is 192 or 194)
            {
                Require(!frame && payload.Length >= 6 && payload[0] == 8);
                int height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                components = payload[5];
                Require(width > 0 && height > 0 && (long)width * height <= maxPixels && components is 1 or 3 or 4 && payload.Length == 6 + 3 * components);
                var ids = new HashSet<byte>();
                for (int i = 6; i < payload.Length; i += 3)
                    Require(ids.Add(payload[i]) && (payload[i + 1] >> 4) is >= 1 and <= 4 && (payload[i + 1] & 15) is >= 1 and <= 4 && payload[i + 2] <= 3);
                frame = true;
            }
            else if (marker == 219)
            {
                int p = 0;
                while (p < payload.Length)
                {
                    int descriptor = payload[p++];
                    Require((descriptor >> 4) <= 1 && (descriptor & 15) <= 3);
                    p += (descriptor >> 4) == 0 ? 64 : 128;
                    Require(p <= payload.Length);
                }
                Require(p > 0); quantization = true;
            }
            else if (marker == 196)
            {
                int p = 0;
                while (p < payload.Length)
                {
                    Require(payload.Length - p >= 17 && (payload[p] >> 4) <= 1 && (payload[p] & 15) <= 3);
                    p++;
                    int symbols = 0, available = 1;
                    for (int i = 0; i < 16; i++) { int count = payload[p++]; available = available * 2 - count; Require(available >= 0); symbols += count; }
                    Require(symbols is > 0 and <= 256 && symbols <= payload.Length - p);
                    p += symbols;
                }
                Require(p > 0); huffman = true;
            }
            else if (marker == 218)
            {
                Require(frame && quantization && huffman && payload.Length >= 6 && payload[0] > 0 && payload[0] <= components && payload.Length == 4 + 2 * payload[0]);
                scan = true;
            }
            else Require(marker is >= 224 and <= 239 or 254 || marker == 221 && payload.Length == 2);
            offset += length;
            if (marker != 218) continue;
            int entropyBytes = 0;
            while (offset < bytes.Length)
            {
                if ((offset & 16383) == 0) token.ThrowIfCancellationRequested();
                if (bytes[offset] != 255) { offset++; entropyBytes++; continue; }
                Require(offset + 1 < bytes.Length);
                int next = bytes[offset + 1];
                if (next == 0 || next is >= 208 and <= 215) { offset += 2; entropyBytes++; continue; }
                break;
            }
            Require(entropyBytes > 0);
        }
        throw new InvalidDataException();
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) == 1 ? 0xedb88320U ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
