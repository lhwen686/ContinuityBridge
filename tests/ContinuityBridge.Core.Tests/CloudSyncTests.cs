using System.Collections.Concurrent;
using System.Security.Cryptography;
using ContinuityBridge.Contracts;
using ContinuityBridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Core.Tests;

[TestClass]
public sealed class CloudSyncTests
{
    [TestMethod]
    public void DwordWrapDuplicatesAndDelayedNotificationsDoNotOrderAsIntegers()
    {
        var sequence = new ClipboardSequence(); sequence.Baseline(uint.MaxValue - 1);
        Assert.IsTrue(sequence.Observe(uint.MaxValue)); Assert.IsTrue(sequence.Observe(0)); Assert.IsTrue(sequence.Observe(1));
        Assert.IsFalse(sequence.Observe(1)); Assert.AreEqual(3L, sequence.Generation);
        // A delayed notification reads the *current* 1 again, never a stale message payload.
        Assert.IsFalse(sequence.Observe(1));
        sequence.Baseline(0); Assert.IsFalse(sequence.Observe(0)); Assert.IsTrue(sequence.Observe(7));
    }

    [TestMethod]
    public async Task StartupClipboardIsNotReadAndRemoteItemIsAppliedOnce()
    {
        await using var run = new Session();
        run.Cloud.Seed("remote fixture"); run.Start();
        await Until(() => run.Clipboard.Applies == 1);
        run.Cloud.Notify(); await Task.Delay(80);
        Assert.AreEqual(0, run.Clipboard.Captures); Assert.IsEmpty(run.Cloud.Puts);
        Assert.AreEqual(1, run.Cloud.Downloads); Assert.AreEqual("remote fixture", run.Clipboard.Text);
    }

    [TestMethod]
    public async Task OwnOldCloudItemIsNotAppliedAndTtlNeverClearsLocal()
    {
        await using var run = new Session();
        run.Cloud.Seed("own old fixture", "windows"); run.Start(); await run.Ready();
        Assert.AreEqual(0, run.Clipboard.Applies);
        run.Clipboard.Copy("fresh fixture"); await Until(() => run.Cloud.Puts.Count == 1);
        run.Cloud.Empty(); run.Cloud.Notify(); await Task.Delay(80);
        Assert.AreEqual("fresh fixture", run.Clipboard.Text); Assert.AreEqual(0, run.Clipboard.Applies);
    }

    [TestMethod]
    public async Task IdenticalContentAfterExpiryIsANewUserAction()
    {
        await using var run = new Session(); run.Start(); await run.Ready();
        run.Clipboard.Copy("same fixture"); await Until(() => run.Cloud.Puts.Count == 1);
        run.Cloud.Empty(); run.Cloud.Notify(); await Task.Delay(60);
        run.Clipboard.Copy("same fixture"); await Until(() => run.Cloud.Puts.Count == 2);
        Assert.AreNotEqual(run.Cloud.Puts.ToArray()[0].Key, run.Cloud.Puts.ToArray()[1].Key);
    }

    [TestMethod]
    public async Task LocalCopyDuringDownloadPreventsOldRemoteWrite()
    {
        await using var run = new Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Cloud.BeforeDownload = async () => { entered.SetResult(); await release.Task; };
        run.Cloud.Seed("old remote"); run.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Clipboard.Copy("new local"); release.SetResult();
        await Until(() => run.Cloud.Puts.Count == 1);
        Assert.AreEqual("new local", run.Clipboard.Text); Assert.AreEqual(0, run.Clipboard.Applies);
    }

    [TestMethod]
    public async Task LostCommitResponseReplaysSameKeyAndNeverRebasesOverAnotherDevice()
    {
        await using var run = new Session(); run.Start(); await run.Ready();
        MutationReceipt? accepted = null;
        run.Cloud.OnPut = (body, etag, key) =>
        {
            if (accepted is null)
            {
                accepted = run.Cloud.Commit(body, key); run.Cloud.Seed("new phone fixture");
                throw new HttpRequestException("synthetic lost response");
            }
            return accepted with { State = run.Cloud.Current, Replayed = true, Available = false };
        };
        run.Clipboard.Copy("local fixture"); await Until(() => run.Statuses.Contains(SyncStatus.Offline));
        run.Cloud.Notify(); await Until(() => run.Cloud.Puts.Count == 2);
        var calls = run.Cloud.Puts.ToArray();
        Assert.AreEqual(calls[0].Key, calls[1].Key); Assert.AreEqual(calls[0].Etag, calls[1].Etag);
        Assert.AreEqual("new phone fixture", run.Cloud.Text); Assert.AreEqual("local fixture", run.Clipboard.Text);
    }

    [TestMethod]
    public async Task OnlineCasConflictRebasesAtMostOnceWithANewKey()
    {
        await using var run = new Session(); run.Start(); await run.Ready();
        run.Cloud.OnPut = (_, _, _) => { run.Cloud.Seed("competing phone"); throw new CloudRequestException(412); };
        run.Clipboard.Copy("local fixture"); await Until(() => run.Statuses.Contains(SyncStatus.Conflict));
        Assert.HasCount(2, run.Cloud.Puts);
        Assert.AreNotEqual(run.Cloud.Puts.ToArray()[0].Key, run.Cloud.Puts.ToArray()[1].Key);
    }

    [TestMethod]
    public async Task OfflineCandidateDoesNotOverwriteChangedCloud()
    {
        await using var run = new Session(); run.Start(); await run.Ready();
        run.Cloud.FailState = true; run.Cloud.Notify(); await Until(() => run.Statuses.Contains(SyncStatus.Offline));
        run.Clipboard.Copy("offline local"); run.Cloud.Seed("phone while offline"); run.Cloud.FailState = false; run.Cloud.Notify();
        await Until(() => run.Statuses.Contains(SyncStatus.OfflineDiscarded));
        Assert.IsEmpty(run.Cloud.Puts); Assert.AreEqual("offline local", run.Clipboard.Text);
    }

    [TestMethod]
    public async Task OfflineAgeLimitAndRestartNeverResurrectOldCopy()
    {
        await using var run = new Session(); run.Start(); await run.Ready();
        run.Cloud.FailState = true; run.Cloud.Notify(); await Until(() => run.Statuses.Contains(SyncStatus.Offline));
        run.Clipboard.Copy("offline fixture"); run.Clock.Advance(TimeSpan.FromSeconds(121));
        run.Cloud.Restart(); run.Cloud.FailState = false; run.Cloud.Notify();
        await Until(() => run.Statuses.Contains(SyncStatus.OfflineDiscarded));
        Assert.IsEmpty(run.Cloud.Puts); Assert.AreEqual("offline fixture", run.Clipboard.Text);
    }

    [TestMethod]
    public async Task DownloadIntegrityFailureAndFinalStateRaceNeverApply()
    {
        await using var run = new Session(); run.Cloud.Seed("expected remote");
        run.Cloud.CorruptDownload = true; run.Start(); await Until(() => run.Statuses.Contains(SyncStatus.InvalidContent));
        Assert.AreEqual(0, run.Clipboard.Applies);
    }

    [TestMethod]
    public async Task PauseCancelsNetworkAndClearsCloudWithoutClearingDevice()
    {
        await using var run = new Session(); run.Start(); await run.Ready();
        run.Clipboard.Copy("device retained"); await Until(() => run.Cloud.Puts.Count == 1);
        run.Engine.RequestClear(); await Until(() => run.Statuses.Contains(SyncStatus.CloudCleared));
        Assert.AreEqual("device retained", run.Clipboard.Text);
        await run.Stop(); int requests = run.Cloud.StateReads;
        run.Clipboard.Copy("after pause"); run.Cloud.Notify(); await Task.Delay(80);
        Assert.AreEqual(requests, run.Cloud.StateReads); Assert.HasCount(1, run.Cloud.Puts);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Session : IAsyncDisposable
    {
        public TestClipboard Clipboard { get; } = new();
        public TestCloud Cloud { get; } = new();
        public TestClock Clock { get; } = new();
        public ConcurrentBag<SyncStatus> Statuses { get; } = [];
        public CloudSyncEngine Engine { get; }
        private readonly CancellationTokenSource cancellation = new();
        private Task? task;
        public Session() { Engine = new(Clipboard, Cloud, Clock, TimeSpan.FromMilliseconds(30)); Engine.StatusChanged += Statuses.Add; }
        public void Start() => task = Engine.RunAsync(cancellation.Token);
        public Task Ready() => Until(() => Statuses.Contains(SyncStatus.Polling));
        public async Task Stop() { await cancellation.CancelAsync(); if (task is not null) await task; }
        public async ValueTask DisposeAsync() { await Stop(); cancellation.Dispose(); }
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class TestClipboard : ICloudClipboard
    {
        public long Generation { get; private set; }
        public string Text { get; private set; } = "pre-start synthetic fixture";
        public int Captures { get; private set; }
        public int Applies { get; private set; }
        public event Action<long>? Changed;
        public void Copy(string value) { Text = value; Generation++; Changed?.Invoke(Generation); }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ClipboardPayload?> CaptureAsync(long expected, Limits limits, CancellationToken cancellationToken)
        { Captures++; return Task.FromResult(expected == Generation ? ClipboardPayload.Text(Text) : null); }
        public Task<bool> TryApplyAsync(ClipboardPayload payload, long expected, Limits limits, CancellationToken cancellationToken)
        {
            if (expected != Generation || cancellationToken.IsCancellationRequested) return Task.FromResult(false);
            Text = ClipboardPayload.StrictUtf8.GetString(payload.Bytes); Applies++; return Task.FromResult(true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestCloud : ICloudTransport
    {
        private int revision;
        private string epoch = Guid.NewGuid().ToString();
        private ClipboardPayload? body;
        private Action? notify;
        public State Current { get; private set; }
        public string? Text => body is null ? null : ClipboardPayload.StrictUtf8.GetString(body.Bytes);
        public ConcurrentQueue<(Guid Key, string Etag)> Puts { get; } = new();
        public int Downloads { get; private set; }
        public int StateReads { get; private set; }
        public bool FailState { get; set; }
        public bool CorruptDownload { get; set; }
        public Func<Task>? BeforeDownload { get; set; }
        public Func<ClipboardPayload, string, Guid, MutationReceipt>? OnPut { get; set; }
        public TestCloud() => Current = MakeState(null);
        private State MakeState(Item? item) => new(1, epoch, revision.ToString(System.Globalization.CultureInfo.InvariantCulture), $"\"{epoch}_{revision}\"", item);
        public void Seed(string value, string device = "phone") => Commit(ClipboardPayload.Text(value), Guid.NewGuid(), device);
        public MutationReceipt Commit(ClipboardPayload value, Guid key, string device = "windows")
        {
            body = value; revision++;
            Current = MakeState(new(Guid.NewGuid().ToString("N"), value.Kind, value.MimeType, value.Bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(value.Bytes)), device, "none", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30)));
            return new(key.ToString(), "put", Current, Current, false, true);
        }
        public void Empty() { body = null; revision++; Current = MakeState(null); }
        public void Restart() { epoch = Guid.NewGuid().ToString(); Empty(); }
        public void Notify() => notify?.Invoke();
        public Task<string> IdentityAsync(CancellationToken cancellationToken) => Task.FromResult("windows");
        public Task<Capabilities> CapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(new Capabilities([1], ["text", "image"], ["image/png", "image/jpeg"],
            new(1_000_000, 8_000_000, 20_000_000, 64_000_000), 1800, new(1800, 512), ["none"], ["poll"], 4096, new()));
        public Task<State> StateAsync(CancellationToken cancellationToken)
        { StateReads++; if (FailState) throw new HttpRequestException("synthetic offline"); return Task.FromResult(Current); }
        public Task<MutationReceipt> PutAsync(ClipboardPayload payload, string etag, Guid key, CancellationToken cancellationToken)
        {
            Puts.Enqueue((key, etag));
            if (OnPut is not null) return Task.FromResult(OnPut(payload, etag, key));
            if (etag != Current.Etag) throw new CloudRequestException(412);
            return Task.FromResult(Commit(payload, key));
        }
        public Task<MutationReceipt> ClearAsync(string etag, Guid key, CancellationToken cancellationToken)
        { Empty(); return Task.FromResult(new MutationReceipt(key.ToString(), "clear", Current, Current, false, false)); }
        public async Task<ClipboardPayload> DownloadAsync(Item item, Limits limits, CancellationToken cancellationToken)
        { Downloads++; var snapshot = body!; if (BeforeDownload is not null) await BeforeDownload(); return CorruptDownload ? ClipboardPayload.Text("corrupt") : snapshot; }
        public async Task WatchAsync(Action changed, Action<bool> connected, CancellationToken cancellationToken)
        { notify = changed; await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        public void Dispose() { }
    }
}
