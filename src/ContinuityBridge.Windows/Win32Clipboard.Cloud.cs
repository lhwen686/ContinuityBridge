using System.Runtime.InteropServices;
using System.Text;
using ContinuityBridge.Contracts;

namespace ContinuityBridge.Windows;

internal sealed partial class Win32Clipboard
{
    private static readonly string[] EncodedFormats = ["PNG", "image/png", "JFIF", "image/jpeg"];
    internal RawClipboard? ReadCloudRaw(uint expected, Limits limits, CancellationToken cancellationToken, bool fixtureOracle = false)
    {
        VerifyThreadAccess();
        return ExecuteWithOpenClipboard(() =>
        {
            if (NativeMethods.GetClipboardSequenceNumber() != expected) return null;
            if (!fixtureOracle && (IsExplicitlyDenied(_includeInHistoryFormat) || IsExplicitlyDenied(_uploadToCloudFormat))) return null;
            // File copies can also advertise thumbnail/image/text alternatives.
            // CF_HDROP excludes the entire copy; never upload a file preview.
            if (NativeMethods.IsClipboardFormatAvailable(15)) return null;
            // Image data precedes text alternatives. Never interpret CF_HDROP as a path to read.
            foreach (var name in EncodedFormats)
            {
                var bytes = ReadBytes(RegisterFormat(name), limits.MaxImageBytes);
                if (bytes is not null) return new RawClipboard(name.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg", bytes);
            }
            // Prefer the first native bitmap representation in the owner's format
            // order. Windows may advertise a synthesized DIBV5 for an original DIB;
            // on the tested desktop that conversion includes redundant mask bytes.
            // Selecting the original DIB avoids shifting its pixel offset by 12 bytes.
            uint format = 0;
            for (int i = 0; i < 128 && (format = NativeMethods.EnumClipboardFormats(format)) != 0; i++)
            {
                if (format is not (2 or 8 or 17)) continue;
                var dib = ReadBytes(format == 2 ? 8u : format, ImageClipboardCodec.MaxRawBytes);
                if (dib is not null) return new RawClipboard("dib", dib);
            }
            // Windows synthesizes CF_DIB from CF_BITMAP through GetClipboardData.
            // Ask explicitly even if only CF_BITMAP was originally advertised.
            if (NativeMethods.IsClipboardFormatAvailable(2))
            {
                var handle = NativeMethods.GetClipboardData(8);
                if (handle == 0) throw new ClipboardUnavailableException("bitmap_conversion_failed");
                return new RawClipboard("dib", CopyBytes(handle, ImageClipboardCodec.MaxRawBytes));
            }
            var text = ReadBytes(NativeMethods.CfUnicodeText, checked(limits.MaxTextUtf8Bytes * 2 + 2));
            return text is null ? null : new RawClipboard("unicode", text);
        }, cancellationToken);
    }

    private static byte[]? ReadBytes(uint format, int limit)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format)) return null;
        var memory = NativeMethods.GetClipboardData(format);
        if (memory == 0) throw new ClipboardUnavailableException("clipboard_data_unavailable");
        return CopyBytes(memory, limit);
    }

    private static byte[] CopyBytes(nint handle, int limit) => WithLockedGlobalMemory(handle, 1, (pointer, size) =>
    {
        if (size > (nuint)limit) throw new InvalidDataException("clipboard_memory_limit");
        byte[] bytes = new byte[(int)size];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return bytes;
    });

    internal bool TryWriteCloud(IReadOnlyList<PreparedClipboard> formats, uint expected, Guid operationId, CancellationToken cancellationToken, bool fixtureSource = false, Action<uint>? committed = null)
    {
        VerifyThreadAccess();
        var entries = new List<(uint Format, nint Memory)>();
        try
        {
            if (!fixtureSource)
            {
            entries.Add((_originFormat, AllocateUnicodeText(operationId.ToString("D"))));
            // Per-item privacy hints do not change the user's global Windows settings.
            entries.Add((_includeInHistoryFormat, AllocateBytes(new byte[4])));
            entries.Add((_uploadToCloudFormat, AllocateBytes(new byte[4])));
            }
            foreach (var entry in formats)
                entries.Add((entry.Format switch { "unicode" => 13, "dib" => 8, "dibv5" => 17, "files" => 15, _ => RegisterFormat(entry.Format) }, AllocateBytes(entry.Bytes)));
            return ExecuteWithOpenClipboard(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (NativeMethods.GetClipboardSequenceNumber() != expected) return false;
                if (!NativeMethods.EmptyClipboard()) throw ClipboardUnavailableException.FromLastPInvokeError("empty_failed");
                try
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        var memory = entry.Memory;
                        TransferClipboardMemory(entry.Format, ref memory);
                        entries[i] = (entry.Format, memory);
                    }
                    committed?.Invoke(NativeMethods.GetClipboardSequenceNumber());
                    return true;
                }
                catch
                {
                    // Win32 has no transactional rollback. Never publish an unmarked
                    // partial item; preallocation makes ordinary allocation failures non-destructive.
                    _ = NativeMethods.EmptyClipboard();
                    throw;
                }
            }, cancellationToken);
        }
        finally
        {
            foreach (var entry in entries) { var memory = entry.Memory; FreeOwnedMemory(ref memory); }
        }
    }

    private static nint AllocateBytes(byte[] bytes)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)bytes.Length);
        if (handle == 0) throw ClipboardUnavailableException.FromLastPInvokeError("allocate_failed");
        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == 0) { _ = NativeMethods.GlobalFree(handle); throw ClipboardUnavailableException.FromLastPInvokeError("lock_failed"); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); UnlockGlobalMemory(handle); return handle; }
        catch { _ = NativeMethods.GlobalUnlock(handle); _ = NativeMethods.GlobalFree(handle); throw; }
    }
}
