using System.Security.Cryptography;

namespace ContinuityBridge.Windows;

// This API is internal to the staging runner and synthetic regression tests.
// No snapshot is a payload, serializable DTO, native handle or IDataObject.
internal interface IClipboardMemory
{
    uint Sequence { get; }
    bool IsOwner { get; }
    uint Format(string name);
    bool Contains(uint format);
    IReadOnlyList<uint> EnumerateFormats();
    byte[] Copy(uint format, int maximumBytes);
    T WithOpen<T>(Func<T> action, CancellationToken cancellationToken);
    IClipboardPublication Prepare(IReadOnlyList<ClipboardMemoryEntry> entries);
}

internal interface IClipboardPublication : IDisposable
{
    // Called only with OpenClipboard held; all memory was allocated beforehand.
    void Publish(Action emptied, CancellationToken cancellationToken);
}

internal sealed class ClipboardMemoryEntry(uint format, byte[] bytes)
{
    internal uint Format { get; } = format;
    internal byte[] Bytes { get; } = bytes;
}

internal enum RestoreOutcome { Untouched, Restored, SkippedExternalCopy, Failed, TimedOut }

internal sealed class ClipboardTakeover(IClipboardMemory memory) : IDisposable
{
    internal const int MaxSnapshotBytes = 96_000_000;
    internal const int MaxFormats = 16;
    private List<ClipboardMemoryEntry>? backup;
    private uint expected;
    private bool touched;
    private bool externalCopy;
    private bool disposed;
    private RestoreOutcome? restored;
    internal uint ExpectedSequence => expected;

    internal void Capture(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (backup is not null) throw new InvalidOperationException("already_captured");
        var copies = new List<ClipboardMemoryEntry>();
        try
        {
            memory.WithOpen(() =>
            {
                uint before = memory.Sequence;
                if (before == 0) throw new InvalidDataException("clipboard_sequence_unavailable");
                var advertised = memory.EnumerateFormats();
                if (advertised.Count > MaxFormats) throw new InvalidDataException("snapshot_format_limit");
                var limits = SupportedFormats();
                // Check the COMPLETE advertised set before fetching any body.
                if (advertised.Any(f => f != 2 && !limits.ContainsKey(f)))
                    throw new InvalidDataException("snapshot_unsupported_format");
                int total = 0;
                foreach (uint format in advertised.Select(f => f == 2 ? 8u : f).Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int limit = Math.Min(limits[format], MaxSnapshotBytes - total);
                    if (limit <= 0) throw new InvalidDataException("snapshot_memory_limit");
                    byte[] bytes = memory.Copy(format, limit);
                    copies.Add(new(format, bytes));
                    if (bytes.Length == 0 || bytes.Length > limit) throw new InvalidDataException("snapshot_memory_limit");
                    total = checked(total + bytes.Length);
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Delayed rendering/conversion can change the sequence: fail closed.
                if (memory.Sequence != before) throw new InvalidDataException("clipboard_changed_during_snapshot");
                expected = before;
                return true;
            }, cancellationToken);
            backup = copies;
        }
        catch { Erase(copies); throw; }
    }

    private Dictionary<uint, int> SupportedFormats() => new()
    {
        [1] = 2_000_002, [7] = 2_000_002, [13] = 2_000_002, [16] = 64,
        [8] = 64_001_024, [17] = 64_001_024,
        [memory.Format("PNG")] = 20_000_000, [memory.Format("image/png")] = 20_000_000,
        [memory.Format("JFIF")] = 20_000_000, [memory.Format("image/jpeg")] = 20_000_000,
        [memory.Format(Win32Clipboard.OriginFormatName)] = 74,
        [memory.Format(Win32Clipboard.IncludeInHistoryFormatName)] = 64,
        [memory.Format(Win32Clipboard.UploadToCloudFormatName)] = 64,
    };

    // May also be called on WM_CLIPBOARDUPDATE; no body read, and sticky on change.
    internal bool IsUnchanged()
    {
        if (backup is null || disposed || externalCopy) return false;
        if (memory.Sequence != expected || touched && !memory.IsOwner) externalCopy = true;
        return !externalCopy;
    }

    internal bool Write(IReadOnlyList<ClipboardMemoryEntry> entries, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (backup is null || restored is not null) throw new InvalidOperationException("takeover_not_active");
        using var publication = memory.Prepare(entries);
        return memory.WithOpen(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnchanged()) return false;
            try { publication.Publish(() => touched = true, cancellationToken); }
            finally
            {
                // Even a partial publication is ours, and eligible for cleanup.
                if (touched) expected = memory.Sequence;
            }
            return true;
        }, cancellationToken);
    }

    internal RestoreOutcome Restore(CancellationToken cancellationToken)
    {
        if (restored is { } previous) return previous;
        if (!touched || backup is null) return (restored = RestoreOutcome.Untouched).Value;
        try
        {
            uint origin = memory.Format(Win32Clipboard.OriginFormatName);
            uint history = memory.Format(Win32Clipboard.IncludeInHistoryFormatName);
            uint upload = memory.Format(Win32Clipboard.UploadToCloudFormatName);
            // Privacy metadata precedes every restored body, including partial failures.
            var entries = new List<ClipboardMemoryEntry>
            {
                new(history, new byte[4]), new(upload, new byte[4]),
                new(origin, System.Text.Encoding.Unicode.GetBytes(Guid.NewGuid().ToString("D") + '\0')),
            };
            entries.AddRange(backup.Where(e => e.Format != origin && e.Format != history && e.Format != upload));
            using var publication = memory.Prepare(entries);
            restored = memory.WithOpen(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsUnchanged()) return RestoreOutcome.SkippedExternalCopy;
                publication.Publish(() => { }, cancellationToken);
                return RestoreOutcome.Restored;
            }, cancellationToken);
        }
        catch (OperationCanceledException) { restored = RestoreOutcome.TimedOut; }
        catch (Exception) { restored = RestoreOutcome.Failed; }
        finally { Dispose(); }
        return restored.Value;
    }

    private static void Erase(IEnumerable<ClipboardMemoryEntry> entries)
    { foreach (var entry in entries) CryptographicOperations.ZeroMemory(entry.Bytes); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (backup is not null) Erase(backup);
        backup = null;
    }
}
