using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ContinuityBridge.Contracts;
using ContinuityBridge.Core;

namespace ContinuityBridge.Windows;

internal static class ImageClipboardCodec
{
    // Bounded native capture plus decode, conversion, three output formats and
    // HGLOBAL transfer copies. Lower than the relay's optional 64MP capability.
    internal const int MaxRawBytes = 80_000_000;
    internal const long MaxPixels = 16_000_000;
    private const long MaxWorkingBytes = 320_000_000;
    internal sealed record Pixels(int Width, int Height, byte[] Bgra);

    internal static ClipboardPayload ToPayload(RawClipboard raw, Limits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClipboardPayload result;
        if (raw.Format == "unicode")
        {
            if (raw.Bytes.Length % 2 != 0) throw new InvalidDataException("invalid_unicode");
            string value = new UnicodeEncoding(false, false, true).GetString(raw.Bytes);
            int end = value.IndexOf('\0', StringComparison.Ordinal);
            if (end < 0 || value.AsSpan(end).IndexOfAnyExcept('\0') >= 0) throw new InvalidDataException("invalid_unicode");
            result = ClipboardPayload.Text(value[..end]);
        }
        else if (raw.Format == "dib")
            result = new("image", "image/png", Encode(DecodeDib(raw.Bytes, limits.MaxDecodedPixels, cancellationToken)));
        else
        {
            string mime = raw.Format == "png" ? "image/png" : "image/jpeg";
            // Probe/trim only zero-filled HGLOBAL allocation padding, never image content.
            byte[] encoded = TrimAllocation(raw.Bytes, mime);
            _ = Decode(encoded, mime, limits.MaxDecodedPixels);
            result = new("image", mime, encoded);
        }
        result.Validate(limits);
        return result;
    }

    internal static IReadOnlyList<PreparedClipboard> Prepare(ClipboardPayload payload, Limits limits, CancellationToken cancellationToken)
    {
        payload.Validate(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Kind == "text") return [new("unicode", Encoding.Unicode.GetBytes(ClipboardPayload.StrictUtf8.GetString(payload.Bytes) + '\0'))];
        var pixels = Decode(payload.Bytes, payload.MimeType, limits.MaxDecodedPixels);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] png = payload.MimeType == "image/png" ? payload.Bytes : Encode(pixels);
        if (png.Length > MaxRawBytes) throw new InvalidDataException("decoded_memory_limit");
        return [new("PNG", png), new("dibv5", Dib(pixels, true)), new("dib", Dib(pixels, false))];
    }

    internal static Pixels Decode(byte[] bytes, string mime, long maximum)
    {
        var (width, height) = Probe(bytes, mime);
        CheckSize(width, height, maximum, bytes.Length);
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("static_image_required");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != width || frame.PixelHeight != height) throw new InvalidDataException("image_dimensions");
        BitmapSource source = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] bgra = new byte[checked(width * height * 4)];
        source.CopyPixels(bgra, checked(width * 4), 0);
        int orientation = 1;
        if (mime == "image/jpeg" && frame.Metadata is BitmapMetadata metadata)
        {
            object? value = metadata.GetQuery("/app1/ifd/{ushort=274}");
            if (value is ushort number) orientation = number;
        }
        return Orient(new(width, height, bgra), orientation);
    }

    internal static byte[] Encode(Pixels pixels)
    {
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra, pixels.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        if (output.Length > MaxRawBytes) throw new InvalidDataException("image_memory_limit");
        return output.ToArray();
    }

    private static void CheckSize(int width, int height, long maximum, int encodedLength)
    {
        long count = (long)width * height;
        if (width <= 0 || height <= 0 || count > Math.Min(MaxPixels, maximum) ||
            count * 18 + (long)encodedLength * 2 > MaxWorkingBytes) throw new InvalidDataException("decoded_memory_limit");
    }

    private static (int Width, int Height) Probe(byte[] bytes, string mime)
    {
        if (mime == "image/png")
        {
            if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("invalid_png");
            int offset = 8; bool ended = false;
            while (offset <= bytes.Length - 12)
            {
                int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
                if (count > bytes.Length - offset - 12) throw new InvalidDataException("invalid_png");
                var type = bytes.AsSpan(offset + 4, 4);
                if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)) throw new InvalidDataException("animation_unsupported");
                offset += count + 12;
                if (type.SequenceEqual("IEND"u8)) { ended = count == 0 && offset == bytes.Length; break; }
            }
            if (!ended || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("invalid_png");
            return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16))), checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20))));
        }
        if (mime != "image/jpeg" || bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216 || bytes[^2] != 255 || bytes[^1] != 217)
            throw new InvalidDataException("invalid_jpeg");
        int p = 2;
        while (p < bytes.Length - 3)
        {
            if (bytes[p++] != 255) throw new InvalidDataException("invalid_jpeg");
            while (p < bytes.Length && bytes[p] == 255) p++;
            if (p >= bytes.Length - 2) break;
            int marker = bytes[p++];
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p));
            if (length < 2 || length > bytes.Length - p) break;
            if (marker is 192 or 194 && length >= 8)
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p + 5)), BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p + 3)));
            if (marker == 218) break;
            p += length;
        }
        throw new InvalidDataException("jpeg_frame_unsupported");
    }

    private static byte[] TrimAllocation(byte[] bytes, string mime)
    {
        int length = bytes.Length;
        if (mime == "image/png" && bytes.Length >= 8)
        {
            int offset = 8;
            while (offset <= bytes.Length - 12)
            {
                int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
                if (count > bytes.Length - offset - 12) break;
                bool end = bytes.AsSpan(offset + 4, 4).SequenceEqual("IEND"u8);
                offset += count + 12;
                if (end) { length = offset; break; }
            }
        }
        else if (mime == "image/jpeg")
        {
            while (length > 2 && bytes[length - 1] == 0) length--;
        }
        if (bytes.AsSpan(length).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("image_trailing_data");
        return length == bytes.Length ? bytes : bytes.AsSpan(0, length).ToArray();
    }

    internal static Pixels DecodeDib(byte[] bytes, long maximum, CancellationToken cancellationToken)
    {
        if (bytes.Length < 40) throw new InvalidDataException("dib_header");
        int header = I32(bytes, 0), width = I32(bytes, 4), signedHeight = I32(bytes, 8);
        if (header is not (40 or 52 or 56 or 108 or 124) || header > bytes.Length || signedHeight == int.MinValue ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)) != 1) throw new InvalidDataException("dib_header");
        int height = Math.Abs(signedHeight);
        CheckSize(width, height, maximum, bytes.Length);
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
        int compression = I32(bytes, 16);
        if (bpp is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3 or 6) ||
            compression != 0 && bpp is not (16 or 32)) throw new InvalidDataException("dib_encoding_unsupported");
        int offset = header;
        uint red = bpp == 16 ? 0x7c00u : 0xff0000u, green = bpp == 16 ? 0x3e0u : 0xff00u, blue = bpp == 16 ? 0x1fu : 0xffu, alpha = 0;
        if (compression is 3 or 6)
        {
            int masks = header == 40 ? offset : 40;
            int maskCount = compression == 6 || header >= 56 ? 4 : 3;
            if (masks + maskCount * 4 > bytes.Length) throw new InvalidDataException("dib_masks");
            red = U32(bytes, masks); green = U32(bytes, masks + 4); blue = U32(bytes, masks + 8);
            if (maskCount == 4) alpha = U32(bytes, masks + 12);
            if (header == 40) offset += maskCount * 4;
            if (red == 0 || green == 0 || blue == 0 || (red & green) != 0 || (red & blue) != 0 || (green & blue) != 0 ||
                (alpha & (red | green | blue)) != 0) throw new InvalidDataException("dib_masks");
        }
        int paletteCount = I32(bytes, 32);
        if (paletteCount < 0 || paletteCount > 256) throw new InvalidDataException("dib_palette");
        if (bpp <= 8 && paletteCount == 0) paletteCount = 1 << bpp;
        int paletteStart = offset;
        offset = checked(offset + paletteCount * 4);
        long strideValue = (((long)width * bpp + 31) / 32) * 4;
        if (strideValue * height > bytes.Length - offset) throw new InvalidDataException("dib_truncated");
        int stride = (int)strideValue;
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = offset + (signedHeight < 0 ? y : height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int target = (y * width + x) * 4;
                if (bpp <= 8)
                {
                    int index = bpp switch { 8 => bytes[row + x], 4 => bytes[row + x / 2] >> (x % 2 == 0 ? 4 : 0) & 15, _ => bytes[row + x / 8] >> (7 - x % 8) & 1 };
                    if (index >= paletteCount) throw new InvalidDataException("dib_palette_index");
                    bytes.AsSpan(paletteStart + index * 4, 3).CopyTo(bgra.AsSpan(target)); bgra[target + 3] = 255;
                }
                else if (bpp == 24) { bytes.AsSpan(row + x * 3, 3).CopyTo(bgra.AsSpan(target)); bgra[target + 3] = 255; }
                else
                {
                    uint pixel = bpp == 16 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(row + x * 2)) : U32(bytes, row + x * 4);
                    bgra[target] = Channel(pixel, blue); bgra[target + 1] = Channel(pixel, green); bgra[target + 2] = Channel(pixel, red);
                    // BI_RGB's reserved high byte is not an alpha channel.
                    bgra[target + 3] = alpha == 0 ? (byte)255 : Channel(pixel, alpha);
                }
            }
        }
        return new(width, height, bgra);
    }

    private static byte Channel(uint pixel, uint mask)
    {
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint maximum = mask >> shift;
        if ((maximum & (maximum + 1)) != 0) throw new InvalidDataException("noncontiguous_mask");
        return (byte)((((ulong)(pixel & mask) >> shift) * 255 + maximum / 2) / maximum);
    }

    internal static byte[] Dib(Pixels pixels, bool alpha)
    {
        int header = alpha ? 124 : 40;
        byte[] bytes = new byte[checked(header + pixels.Bgra.Length)];
        W32(bytes, 0, header); W32(bytes, 4, pixels.Width); W32(bytes, 8, -pixels.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 32);
        W32(bytes, 16, alpha ? 3 : 0); W32(bytes, 20, pixels.Bgra.Length);
        if (alpha)
        {
            W32(bytes, 40, 0xff0000); W32(bytes, 44, 0xff00); W32(bytes, 48, 0xff); W32(bytes, 52, unchecked((int)0xff000000));
            W32(bytes, 56, 0x73524742); W32(bytes, 108, 4); // sRGB, LCS_GM_IMAGES
            pixels.Bgra.CopyTo(bytes, header);
        }
        else
        {
            // Legacy DIB cannot carry alpha: composite on white. PNG/DIBV5 retain it.
            for (int i = 0; i < pixels.Bgra.Length; i += 4)
            {
                int a = pixels.Bgra[i + 3];
                for (int c = 0; c < 3; c++) bytes[header + i + c] = (byte)((pixels.Bgra[i + c] * a + 255 * (255 - a) + 127) / 255);
                bytes[header + i + 3] = 0;
            }
        }
        return bytes;
    }

    internal static Pixels Orient(Pixels input, int orientation)
    {
        if (orientation == 1) return input;
        if (orientation is < 1 or > 8) throw new InvalidDataException("image_orientation");
        int width = orientation >= 5 ? input.Height : input.Width, height = orientation >= 5 ? input.Width : input.Height;
        byte[] output = new byte[input.Bgra.Length];
        for (int y = 0; y < input.Height; y++)
            for (int x = 0; x < input.Width; x++)
            {
                var (dx, dy) = orientation switch
                {
                    2 => (input.Width - 1 - x, y), 3 => (input.Width - 1 - x, input.Height - 1 - y),
                    4 => (x, input.Height - 1 - y), 5 => (y, x), 6 => (input.Height - 1 - y, x),
                    7 => (input.Height - 1 - y, input.Width - 1 - x), _ => (y, input.Width - 1 - x)
                };
                input.Bgra.AsSpan((y * input.Width + x) * 4, 4).CopyTo(output.AsSpan((dy * width + dx) * 4));
            }
        return new(width, height, output);
    }

    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void W32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
}
