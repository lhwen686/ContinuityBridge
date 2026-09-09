using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using ContinuityBridge.Contracts;
using ContinuityBridge.Relay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]
namespace ContinuityBridge.Relay.Tests;

[TestClass]
public sealed class StateTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static RelayOptions Options(int entries = 512, int idempotencyTtl = 10) => new()
    { DeviceFile = "unused", RetentionSeconds = 10, IdempotencyRetentionSeconds = idempotencyTtl, IdempotencyMaxEntries = entries };
    private static MutationReceipt Put(ClipboardStore store, byte[]? body = null) => store.Commit("fixture-device", Guid.NewGuid(),
        Guid.NewGuid().ToString(), store.GetState().Etag, body ?? "fixture"u8.ToArray(), "text/plain; charset=utf-8");

    [TestMethod]
    public void ExactExpiryAndConcurrentSweepsAdvanceOnlyOnce()
    {
        var clock = new Clock();
        using var store = new ClipboardStore(Options(), clock);
        var put = Put(store);
        clock.Now = new DateTimeOffset(put.Result.Item!.ExpiresAt).AddTicks(-1);
        using var read = store.Open(put.Result.Item.ItemId);
        Assert.AreEqual(7, read.Body.Length);
        clock.Now = clock.Now.AddTicks(1);
        Parallel.For(0, 32, _ => Assert.IsNull(store.GetState().Item));
        Assert.AreEqual("2", store.GetState().Revision);
        Assert.IsTrue(read.Retired.IsCancellationRequested);
        Assert.AreEqual(410, Assert.ThrowsExactly<ProtocolException>(() => store.Open(put.Result.Item.ItemId)).Status);
        Assert.AreEqual("2", store.GetState().Revision);
    }

    [TestMethod]
    public void ReplayAfterReplacementDoesNotResurrectOrRenew()
    {
        var clock = new Clock();
        using var store = new ClipboardStore(Options(), clock);
        var initial = store.GetState(); var key = Guid.NewGuid();
        var a = store.Commit("device-a", key, "fingerprint-a", initial.Etag, "A"u8.ToArray(), "text/plain; charset=utf-8");
        using var read = store.Open(a.Result.Item!.ItemId);
        clock.Now = clock.Now.AddSeconds(1);
        var b = Put(store);
        var replay = store.Commit("device-a", key, "fingerprint-a", initial.Etag, "A"u8.ToArray(), "text/plain; charset=utf-8");
        Assert.IsTrue(replay.Replayed); Assert.IsFalse(replay.Available);
        Assert.AreEqual(a.Result, replay.Result); Assert.AreEqual(b.State, replay.State);
        Assert.IsTrue(read.Retired.IsCancellationRequested);
        Assert.AreEqual(409, Assert.ThrowsExactly<ProtocolException>(() => store.Commit("device-a", key, "changed", initial.Etag, [], "image/png")).Status);
    }

    [TestMethod]
    public void ConcurrentCasAndSameKeyAreAtomic()
    {
        using var store = new ClipboardStore(Options(), new Clock());
        string etag = store.GetState().Etag;
        var statuses = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 24, i =>
        {
            try { store.Commit("device-" + i, Guid.NewGuid(), "fp", etag, [1], "image/png"); statuses.Add(200); }
            catch (ProtocolException ex) { statuses.Add(ex.Status); }
        });
        Assert.AreEqual(1, statuses.Count(x => x == 200)); Assert.AreEqual(23, statuses.Count(x => x == 412));
        etag = store.GetState().Etag; var key = Guid.NewGuid();
        var results = new System.Collections.Concurrent.ConcurrentBag<MutationReceipt>();
        Parallel.For(0, 24, _ => results.Add(store.Commit("device", key, "fp", etag, [2], "image/png")));
        Assert.AreEqual(1, results.Count(r => !r.Replayed)); Assert.AreEqual("2", store.GetState().Revision);
    }

    [TestMethod]
    public void EvictionExpiryAndNewEpochRejectOldConditions()
    {
        var clock = new Clock();
        using var store = new ClipboardStore(Options(entries: 1, idempotencyTtl: 2), clock);
        var key = Guid.NewGuid(); string etag = store.GetState().Etag;
        store.Commit("device", key, "fp", etag, [1], "image/png");
        Put(store);
        Assert.AreEqual(412, Assert.ThrowsExactly<ProtocolException>(() => store.Commit("device", key, "fp", etag, [1], "image/png")).Status);
        string current = store.GetState().Etag; key = Guid.NewGuid();
        store.Commit("device", key, "fp", current, [1], "image/png");
        clock.Now = clock.Now.AddSeconds(2);
        Assert.AreEqual(412, Assert.ThrowsExactly<ProtocolException>(() => store.Commit("device", key, "fp", current, [1], "image/png")).Status);
        using var restart = new ClipboardStore(Options(), clock);
        Assert.AreNotEqual(store.GetState().ServerEpoch, restart.GetState().ServerEpoch);
        Assert.AreEqual(412, Assert.ThrowsExactly<ProtocolException>(() => restart.Commit("device", key, "fp", current, [1], "image/png")).Status);
    }

    [TestMethod]
    public void ClearEmptyStillAdvancesAndCancellationPreservesSlot()
    {
        using var store = new ClipboardStore(Options(), new Clock());
        var first = store.GetState();
        var clear = store.Commit("device", Guid.NewGuid(), "fp", first.Etag, null, "");
        Assert.AreEqual("1", clear.State.Revision); Assert.IsFalse(clear.Available);
        var put = Put(store);
        Assert.ThrowsExactly<OperationCanceledException>(() => store.Commit("device", Guid.NewGuid(), "fp2", put.State.Etag, [3], "image/png", new CancellationToken(true)));
        Assert.AreEqual(put.State, store.GetState());
    }

    [TestMethod]
    public void CacheObjectGraphContainsOnlyMetadataAndRetiredBodyIsCollectible()
    {
        using var store = new ClipboardStore(Options(), new Clock());
        WeakReference retired = CreateAndReplace(store);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsFalse(retired.IsAlive, "Completed requests must not retain replaced body arrays.");
        object cache = typeof(ClipboardStore).GetField("completed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        Walk(cache, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndReplace(ClipboardStore store)
    {
        byte[] bytes = new byte[2_000_000];
        Put(store, bytes);
        var reference = new WeakReference(bytes);
        Put(store, [1]);
        return reference;
    }

    private static void Walk(object? node, HashSet<object> seen)
    {
        if (node is null || !seen.Add(node)) return;
        Assert.IsFalse(node is byte[] or ClipboardStore.DownloadLease, "Body reachable from idempotency metadata.");
        Type type = node.GetType();
        if (node is string || type.IsPrimitive || type.IsEnum || node is DateTime or Guid) return;
        if (node is IEnumerable collection) { foreach (var item in collection) Walk(item, seen); return; }
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        { Assert.IsFalse(field.Name.Contains("Snapshot", StringComparison.Ordinal)); Walk(field.GetValue(node), seen); }
    }

    [TestMethod]
    public void InvalidLimitsFailBeforeListening()
    {
        Options().Validate();
        Assert.ThrowsExactly<InvalidOperationException>(() => new RelayOptions { DeviceFile = "unused", MaxTextJsonBytes = 1 }.Validate());
        Assert.ThrowsExactly<InvalidOperationException>(() => new RelayOptions { DeviceFile = "unused", RetentionSeconds = 1 }.Validate());
        Assert.ThrowsExactly<InvalidOperationException>(() => new RelayOptions { DeviceFile = "unused", MemoryBudgetBytes = 1 }.Validate());
    }

    [TestMethod]
    public void ExpiryAndDisposeReleaseBodyEvenWhileReceiptIsRetained()
    {
        var clock = new Clock();
        using var store = new ClipboardStore(Options(), clock);
        var (reference, receipt) = CreateBody(store);
        clock.Now = clock.Now.AddSeconds(10);
        Assert.IsNull(store.GetState().Item);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsFalse(reference.IsAlive);
        Assert.IsNotNull(receipt.Result.Item);
        var (second, _) = CreateBody(store);
        store.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsFalse(second.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, MutationReceipt) CreateBody(ClipboardStore store)
    {
        byte[] body = new byte[2_000_000];
        return (new WeakReference(body), Put(store, body));
    }
}
