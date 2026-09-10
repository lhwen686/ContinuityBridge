using ContinuityBridge.Contracts;
using ContinuityBridge.Core;

namespace ContinuityBridge.Windows;

public sealed class CloudClipboardAdapter : ICloudClipboard
{
    private readonly WindowsClipboardAdapter host;
    private readonly ClipboardSequence sequence = new();
    private long generation;
    private bool initialized;

    public CloudClipboardAdapter(Action<Exception>? errorHandler = null) =>
        host = new WindowsClipboardAdapter((Win32Clipboard _) => Observe(), errorHandler ?? (_ => { }), cloudMode: true);

    public long Generation => Interlocked.Read(ref generation);
    public event Action<long>? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        await host.InvokeOnClipboardThreadAsync(_ =>
        {
            if (!initialized)
            {
                // Never materialize or upload the pre-start clipboard.
                sequence.Baseline(NativeMethods.GetClipboardSequenceNumber());
                initialized = true;
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Observe()
    {
        if (!initialized || !sequence.Observe(NativeMethods.GetClipboardSequenceNumber())) return;
        Interlocked.Exchange(ref generation, sequence.Generation);
        Changed?.Invoke(sequence.Generation);
    }

    public async Task<ClipboardPayload?> CaptureAsync(long expected, Limits limits, CancellationToken cancellationToken)
    {
        // Preserve the original 75ms settle window for OLE delayed rendering.
        // Generation protection is immediate; only materialization is debounced.
        await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        RawClipboard? raw = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                raw = await host.InvokeOnClipboardThreadAsync(clipboard =>
                {
                    Observe();
                    if (Generation != expected) return null;
                    return clipboard.ReadCloudRaw(sequence.Current, limits, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (ClipboardUnavailableException ex)
            {
                if (attempt == 2) throw new InvalidDataException("clipboard_unavailable", ex);
                await Task.Delay(50 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
        if (raw is null || expected != Generation) return null;
        // Even when invoked from a UI context, image decode/encode stays off STA.
        return await Task.Run(() => ImageClipboardCodec.ToPayload(raw, limits, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryApplyAsync(ClipboardPayload payload, long expected, Limits limits, CancellationToken cancellationToken)
    {
        var formats = await Task.Run(() => ImageClipboardCodec.Prepare(payload, limits, cancellationToken), cancellationToken).ConfigureAwait(false);
        return await host.InvokeOnClipboardThreadAsync(clipboard =>
        {
            Observe();
            if (Generation != expected) return false;
            // Check the live DWORD *inside* OpenClipboard, holding it through Empty/
            // marker/all-format publication. A queued WM message cannot hide a copy.
            var written = clipboard.TryWriteCloud(formats, sequence.Current, Guid.NewGuid(), cancellationToken, committed: sequence.Baseline);
            if (!written) { Observe(); return false; }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();
}

internal sealed record RawClipboard(string Format, byte[] Bytes);
internal sealed record PreparedClipboard(string Format, byte[] Bytes);
