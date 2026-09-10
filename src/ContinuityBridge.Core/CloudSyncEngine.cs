using System.Threading.Channels;
using ContinuityBridge.Contracts;

namespace ContinuityBridge.Core;

public enum SyncStatus { Connecting, Connected, Polling, LocalSent, RemoteApplied, Unsupported,
    Offline, OfflineDiscarded, Conflict, AuthenticationRequired, InvalidContent, Paused, CloudCleared }

// A single worker owns all bodies and mutations. The STA callback only publishes
// the latest intent and cancels stale work; it never encodes or performs HTTP.
public sealed class CloudSyncEngine
{
    private readonly ICloudClipboard clipboard;
    private readonly ICloudTransport transport;
    private readonly TimeProvider clock;
    private readonly TimeSpan pollInterval;
    private readonly object gate = new();
    private readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private State? observed;
    private Intent? intent;
    private CancellationTokenSource? transfer;
    private bool online;
    private bool websocket;
    private bool clearRequested;

    public CloudSyncEngine(ICloudClipboard clipboard, ICloudTransport transport, TimeProvider? clock = null, TimeSpan? pollInterval = null)
    {
        this.clipboard = clipboard; this.transport = transport;
        this.clock = clock ?? TimeProvider.System;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public event Action<SyncStatus>? StatusChanged;
    public void RequestClear() { lock (gate) clearRequested = true; Signal(); }
    private void Signal() => wake.Writer.TryWrite(0);

    private void LocalChanged(long generation)
    {
        lock (gate)
        {
            intent = new(generation, clock.GetTimestamp(), observed, online);
            transfer?.Cancel();
        }
        Signal();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        clipboard.Changed += LocalChanged;
        Task? watch = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Pending? pending = null;
        (string Etag, Guid Key)? clear = null;
        string? handledEtag = null;
        long processedGeneration = -1;
        int failures = 0;
        Capabilities? capabilities = null;
        string? deviceId = null;
        long capsAt = 0;
        try
        {
            StatusChanged?.Invoke(SyncStatus.Connecting);
            await clipboard.StartAsync(lifetime.Token).ConfigureAwait(false);
            watch = transport.WatchAsync(Signal, connected => { lock (gate) websocket = connected; Signal(); }, lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                long cycleGeneration = clipboard.Generation;
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (gate) transfer = operation;
                try
                {
                    if (capabilities is null || clock.GetElapsedTime(capsAt) > TimeSpan.FromMinutes(1))
                    {
                        capabilities = await transport.CapabilitiesAsync(operation.Token).ConfigureAwait(false);
                        deviceId = await transport.IdentityAsync(operation.Token).ConfigureAwait(false);
                        capsAt = clock.GetTimestamp();
                    }
                    var state = await transport.StateAsync(operation.Token).ConfigureAwait(false);
                    Intent? current;
                    bool doClear;
                    lock (gate)
                    {
                        if (observed is not null && observed.ServerEpoch != state.ServerEpoch) capabilities = null;
                        observed = state; online = true; current = intent;
                        doClear = clearRequested; clearRequested = false;
                    }
                    if (doClear && clear is null) clear = (state.Etag, Guid.NewGuid());
                    // An epoch change must refresh limits before handling a body.
                    if (capabilities is null) { Signal(); continue; }
                    failures = 0;
                    if (current is not null && current.Generation > processedGeneration)
                    {
                        pending = null;
                        processedGeneration = current.Generation;
                        handledEtag = state.Etag; // Local intent protects against this recovery snapshot.
                        if (current.Generation == clipboard.Generation)
                        {
                            var body = await clipboard.CaptureAsync(current.Generation, capabilities.Limits, operation.Token).ConfigureAwait(false);
                            if (body is null) StatusChanged?.Invoke(SyncStatus.Unsupported);
                            else if (current.Basis is null || Expired(current)) StatusChanged?.Invoke(SyncStatus.OfflineDiscarded);
                            else
                            {
                                body.Validate(capabilities.Limits);
                                pending = new(current, body, current.Basis.Etag, Guid.NewGuid());
                            }
                        }
                    }
                    if (pending is not null)
                    {
                        handledEtag = state.Etag;
                        if (pending.Intent.Generation != clipboard.Generation || Expired(pending.Intent))
                        { pending = null; StatusChanged?.Invoke(SyncStatus.OfflineDiscarded); }
                        else if (!pending.Intent.Online && !pending.Attempted && pending.Etag != state.Etag)
                        { pending = null; StatusChanged?.Invoke(SyncStatus.OfflineDiscarded); }
                        else
                        {
                            try
                            {
                                // Even after an unknown outcome replay the original key/condition/body.
                                // A stale condition can only replay an existing receipt or fail CAS.
                                pending.Attempted = true;
                                var receipt = await transport.PutAsync(pending.Body, pending.Etag, pending.Key, operation.Token).ConfigureAwait(false);
                                lock (gate) observed = receipt.State;
                                handledEtag = receipt.State.Etag;
                                pending = null;
                                StatusChanged?.Invoke(SyncStatus.LocalSent);
                            }
                            catch (CloudRequestException ex) when (ex.StatusCode == 412 && pending is not null)
                            {
                                if (pending.Intent.Online && !pending.HadNetworkFailure && !pending.Rebased &&
                                    pending.Intent.Generation == clipboard.Generation && !Expired(pending.Intent))
                                {
                                    var fresh = await transport.StateAsync(operation.Token).ConfigureAwait(false);
                                    // An instance restart is never an invitation to resurrect an old body.
                                    if (fresh.ServerEpoch == pending.Intent.Basis!.ServerEpoch)
                                    {
                                        pending.Etag = fresh.Etag; pending.Key = Guid.NewGuid(); pending.Rebased = true;
                                        lock (gate) observed = fresh;
                                        Signal();
                                    }
                                    else { pending = null; StatusChanged?.Invoke(SyncStatus.OfflineDiscarded); }
                                }
                                else { pending = null; StatusChanged?.Invoke(SyncStatus.Conflict); }
                            }
                        }
                        continue;
                    }

                    if (clear is { } clearOperation)
                    {
                        try
                        {
                            var result = await transport.ClearAsync(clearOperation.Etag, clearOperation.Key, operation.Token).ConfigureAwait(false);
                            lock (gate) observed = result.State;
                            handledEtag = result.State.Etag;
                            clear = null; StatusChanged?.Invoke(SyncStatus.CloudCleared);
                        }
                        catch (CloudRequestException ex) when (ex.StatusCode == 412)
                        { clear = null; StatusChanged?.Invoke(SyncStatus.Conflict); }
                        continue;
                    }

                    if (state.Etag != handledEtag && cycleGeneration == clipboard.Generation)
                    {
                        var item = state.Item;
                        if (item is null || item.SourceDeviceId == deviceId || item.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
                            handledEtag = state.Etag;
                        else
                        {
                            var body = await transport.DownloadAsync(item, capabilities.Limits, operation.Token).ConfigureAwait(false);
                            if (!body.Matches(item)) throw new InvalidDataException("content_integrity");
                            var final = await transport.StateAsync(operation.Token).ConfigureAwait(false);
                            lock (gate) observed = final;
                            if (final.Etag == state.Etag && final.ServerEpoch == state.ServerEpoch &&
                                final.Item?.ItemId == item.ItemId && item.ExpiresAt > clock.GetUtcNow().UtcDateTime &&
                                await clipboard.TryApplyAsync(body, cycleGeneration, capabilities.Limits, operation.Token).ConfigureAwait(false))
                            {
                                handledEtag = final.Etag;
                                StatusChanged?.Invoke(SyncStatus.RemoteApplied);
                            }
                            else Signal();
                        }
                    }
                    lock (gate) StatusChanged?.Invoke(websocket ? SyncStatus.Connected : SyncStatus.Polling);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && operation.IsCancellationRequested)
                { Signal(); }
                catch (CloudRequestException ex) when (ex.StatusCode is 401 or 403)
                {
                    pending = null; clear = null;
                    lock (gate) online = false;
                    StatusChanged?.Invoke(SyncStatus.AuthenticationRequired);
                    // Revocation requires an explicit resume/configuration action.
                    break;
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException ||
                    ex is CloudRequestException bad && bad.StatusCode is not (408 or 429 or >= 500))
                {
                    pending = null;
                    StatusChanged?.Invoke(SyncStatus.InvalidContent);
                    failures = 1;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException ||
                    ex is CloudRequestException error && error.StatusCode is 408 or 429 or >= 500)
                {
                    if (pending is not null) pending.HadNetworkFailure = true;
                    lock (gate) online = false;
                    failures = Math.Min(failures + 1, 5);
                    StatusChanged?.Invoke(SyncStatus.Offline);
                }
                finally { lock (gate) transfer = null; }

                TimeSpan delay = failures == 0 ? pollInterval : TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, failures - 1))) +
                    TimeSpan.FromMilliseconds(Random.Shared.Next(500));
                // A single cancellable channel wait avoids leaking waiters on each poll.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                wait.CancelAfter(delay);
                try { _ = await wake.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            clipboard.Changed -= LocalChanged;
            lock (gate) { transfer = null; online = false; intent = null; observed = null; }
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (watch is not null)
                try { await watch.ConfigureAwait(false); } catch (OperationCanceledException) { }
            pending = null;
            if (cancellationToken.IsCancellationRequested) StatusChanged?.Invoke(SyncStatus.Paused);
        }
    }

    private bool Expired(Intent value) => clock.GetElapsedTime(value.Timestamp) > TimeSpan.FromSeconds(120);
    private sealed record Intent(long Generation, long Timestamp, State? Basis, bool Online);
    private sealed class Pending(Intent intent, ClipboardPayload body, string etag, Guid key)
    {
        public Intent Intent { get; } = intent;
        public ClipboardPayload Body { get; } = body;
        public string Etag { get; set; } = etag;
        public Guid Key { get; set; } = key;
        public bool Attempted { get; set; }
        public bool Rebased { get; set; }
        public bool HadNetworkFailure { get; set; }
    }
}
