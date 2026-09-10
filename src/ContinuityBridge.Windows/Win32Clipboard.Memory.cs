using System.Runtime.InteropServices;

namespace ContinuityBridge.Windows;

internal sealed partial class Win32Clipboard : IClipboardMemory
{
    uint IClipboardMemory.Sequence => NativeMethods.GetClipboardSequenceNumber();
    bool IClipboardMemory.IsOwner => NativeMethods.GetClipboardOwner() == _ownerWindow;
    uint IClipboardMemory.Format(string name) => RegisterFormat(name);
    bool IClipboardMemory.Contains(uint format) => NativeMethods.IsClipboardFormatAvailable(format);
    byte[] IClipboardMemory.Copy(uint format, int maximumBytes)
    {
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == 0) throw new ClipboardUnavailableException("snapshot_data_unavailable");
        return CopyBytes(handle, maximumBytes);
    }
    T IClipboardMemory.WithOpen<T>(Func<T> action, CancellationToken cancellationToken)
    { VerifyThreadAccess(); return ExecuteWithOpenClipboard(action, cancellationToken); }

    IReadOnlyList<uint> IClipboardMemory.EnumerateFormats()
    {
        var formats = new List<uint>();
        uint format = 0;
        while (true)
        {
            Marshal.SetLastPInvokeError(0);
            format = NativeMethods.EnumClipboardFormats(format);
            if (format == 0)
            {
                if (Marshal.GetLastPInvokeError() != 0) throw new ClipboardUnavailableException("snapshot_enumeration_failed");
                return formats;
            }
            formats.Add(format);
            if (formats.Count > ClipboardTakeover.MaxFormats) throw new InvalidDataException("snapshot_format_limit");
        }
    }

    IClipboardPublication IClipboardMemory.Prepare(IReadOnlyList<ClipboardMemoryEntry> entries) => new Publication(entries);

    private sealed class Publication : IClipboardPublication
    {
        private readonly List<(uint Format, nint Memory)> entries = [];
        internal Publication(IReadOnlyList<ClipboardMemoryEntry> source)
        {
            try { foreach (var entry in source) entries.Add((entry.Format, AllocateBytes(entry.Bytes))); }
            catch { Dispose(); throw; }
        }
        public void Publish(Action emptied, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.EmptyClipboard()) throw new ClipboardUnavailableException("empty_failed");
            emptied();
            // Do not clear successful privacy markers if a later format fails.
            for (int i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i]; var handle = entry.Memory;
                TransferClipboardMemory(entry.Format, ref handle);
                entries[i] = (entry.Format, handle);
            }
        }
        public void Dispose()
        { foreach (var entry in entries) { var handle = entry.Memory; FreeOwnedMemory(ref handle); } entries.Clear(); }
    }
}
