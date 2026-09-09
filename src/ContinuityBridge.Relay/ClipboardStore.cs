using System.Globalization;
using System.Numerics;
using ContinuityBridge.Contracts;

namespace ContinuityBridge.Relay;

public sealed class ClipboardStore(RelayOptions options, TimeProvider clock) : IDisposable
{
    private readonly object gate = new();
    private readonly string epoch = Guid.NewGuid().ToString();
    private BigInteger revision;
    private BodySlot? current;
    // No text, byte[], body owner, download lease or callback in this cache.
    private sealed record Completed(string Fingerprint, string Operation, State Result, DateTime ExpiresAt);
    private readonly Dictionary<(string Device, Guid Key), Completed> completed = [];

    private State Snapshot() => new(1, epoch, revision.ToString(CultureInfo.InvariantCulture),
        $"\"{epoch.Replace("-", "", StringComparison.Ordinal)}_{revision.ToString(CultureInfo.InvariantCulture)}\"", current?.Metadata);

    private void Sweep(DateTime now)
    {
        if (current is not null && now >= current.Metadata.ExpiresAt)
        {
            current.Retire();
            current = null;
            revision++;
        }
        foreach (var key in completed.Where(pair => now >= pair.Value.ExpiresAt).Select(pair => pair.Key).ToArray())
            completed.Remove(key);
    }

    public State GetState()
    {
        lock (gate) { Sweep(clock.GetUtcNow().UtcDateTime); return Snapshot(); }
    }

    // Ownership of body transfers only on a new successful put. The HTTP admission lease
    // covers validation, commit, and receipt serialization; it never queues unlimited bodies.
    public MutationReceipt Commit(string device, Guid key, string fingerprint, string expected,
        byte[]? body, string mime, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime now = clock.GetUtcNow().UtcDateTime;
            Sweep(now);
            if (completed.TryGetValue((device, key), out var previous))
            {
                if (previous.Fingerprint != fingerprint) throw new ProtocolException(409, "idempotency_conflict");
                return Receipt(key, previous, true);
            }
            if (Snapshot().Etag != expected) throw new ProtocolException(412, "state_conflict");
            BodySlot? replacement = null;
            if (body is not null)
            {
                var item = new Item(Guid.NewGuid().ToString("N"), mime.StartsWith("image/", StringComparison.Ordinal) ? "image" : "text",
                    mime, body.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)),
                    device, "none", now, now.AddSeconds(options.RetentionSeconds));
                cancellationToken.ThrowIfCancellationRequested();
                replacement = new BodySlot(item, body);
            }
            current?.Retire();
            current = replacement;
            revision++;
            var result = new Completed(fingerprint, body is null ? "clear" : "put", Snapshot(), now.AddSeconds(options.IdempotencyRetentionSeconds));
            if (completed.Count >= options.IdempotencyMaxEntries)
                completed.Remove(completed.MinBy(pair => pair.Value.ExpiresAt).Key);
            completed.Add((device, key), result);
            return Receipt(key, result, false);
        }
    }

    private MutationReceipt Receipt(Guid key, Completed result, bool replayed) => new(key.ToString(), result.Operation,
        result.Result, Snapshot(), replayed, result.Result.Item is not null && result.Result.Item.ItemId == current?.Metadata.ItemId);

    public DownloadLease Open(string id)
    {
        lock (gate)
        {
            Sweep(clock.GetUtcNow().UtcDateTime);
            if (current is null || current.Metadata.ItemId != id) throw new ProtocolException(410, "item_unavailable");
            return current.Acquire();
        }
    }

    public void Dispose()
    {
        lock (gate) { current?.Retire(); current = null; completed.Clear(); }
    }

    internal sealed class BodySlot(Item metadata, byte[] body) : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource retired = new();
        private byte[]? bytes = body;
        private int readers;
        public Item Metadata { get; } = metadata;
        public DownloadLease Acquire()
        {
            lock (gate) { readers++; return new DownloadLease(this, bytes!, retired.Token); }
        }
        public void Retire()
        {
            lock (gate)
            {
                bytes = null;
                retired.Cancel();
                if (readers == 0) retired.Dispose();
            }
        }
        public void Dispose() => Retire();
        public void Release()
        {
            lock (gate) { if (--readers == 0 && bytes is null) retired.Dispose(); }
        }
    }

    public sealed class DownloadLease : IDisposable
    {
        private BodySlot? owner;
        public ReadOnlyMemory<byte> Body { get; private set; }
        public Item Metadata { get; }
        public CancellationToken Retired { get; }
        internal DownloadLease(BodySlot owner, byte[] body, CancellationToken retired)
        { this.owner = owner; Body = body; Metadata = owner.Metadata; Retired = retired; }
        public void Dispose() { Body = default; Interlocked.Exchange(ref owner, null)?.Release(); }
    }
}
