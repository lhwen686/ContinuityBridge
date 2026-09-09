using System.Security.Cryptography;
using System.Text;
using ContinuityBridge.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Core.Tests;

[TestClass]
public sealed class StateCoordinatorTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 23, 6, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Local_S1_Then_Remote_S2_RemoteWins()
    {
        var writer = new RecordingClipboardWriter(
            (call, _, _) => Task.FromResult(new ClipboardWriteResult((uint)(call + 1))));
        await using var coordinator = new StateCoordinator(writer);

        Assert.IsTrue(coordinator.TryEnqueueLocal(Local(1, "local")));
        await WaitUntilAsync(() => coordinator.LatestSnapshot?.WindowsSequence == 1);

        var result = await coordinator.SubmitRemoteAsync(Remote("remote"));

        Assert.AreEqual(2L, result.Snapshot.Revision);
        Assert.AreEqual(ClipboardOrigin.IPhoneRemote, result.Snapshot.Origin);
        Assert.AreEqual(2U, result.Snapshot.WindowsSequence);
        Assert.AreEqual("remote", result.Snapshot.Text);
        Assert.AreEqual(result.Snapshot, coordinator.LatestSnapshot);
    }

    [TestMethod]
    public async Task Remote_S1_Then_Local_S2_LocalWins()
    {
        var writer = new RecordingClipboardWriter();
        await using var coordinator = new StateCoordinator(writer);

        var remote = await coordinator.SubmitRemoteAsync(Remote("remote"));
        Assert.AreEqual(1U, remote.Snapshot.WindowsSequence);

        Assert.IsTrue(coordinator.TryEnqueueLocal(Local(2, "local")));
        await WaitUntilAsync(() => coordinator.LatestSnapshot?.WindowsSequence == 2);

        var latest = coordinator.LatestSnapshot;
        Assert.IsNotNull(latest);
        Assert.AreEqual(2L, latest.Revision);
        Assert.AreEqual(ClipboardOrigin.WindowsLocal, latest.Origin);
        Assert.AreEqual("local", latest.Text);
    }

    [TestMethod]
    public async Task StaleSequence_IsDiscarded()
    {
        var writer = new RecordingClipboardWriter(
            (_, _, _) => Task.FromResult(new ClipboardWriteResult(2)));
        await using var coordinator = new StateCoordinator(writer);

        var remote = await coordinator.SubmitRemoteAsync(Remote("newer"));
        Assert.IsTrue(coordinator.TryEnqueueLocal(Local(1, "stale")));

        await coordinator.DisposeAsync();

        Assert.AreEqual(remote.Snapshot, coordinator.LatestSnapshot);
        Assert.AreEqual(1L, coordinator.Revision);
        Assert.AreEqual(2U, coordinator.LastCommittedWindowsSequence);
    }

    [TestMethod]
    public async Task DuplicateRequestId_ReturnsOriginalRevision()
    {
        var writer = new RecordingClipboardWriter();
        await using var coordinator = new StateCoordinator(writer);
        var requestId = Guid.NewGuid();

        var first = await coordinator.SubmitRemoteAsync(
            new RemoteClipboardCommand(requestId, "first"));
        var duplicate = await coordinator.SubmitRemoteAsync(
            new RemoteClipboardCommand(requestId, "must-not-replace-first"));

        Assert.AreEqual(first, duplicate);
        Assert.AreEqual(1L, duplicate.Snapshot.Revision);
        Assert.AreEqual("first", coordinator.LatestSnapshot?.Text);
        Assert.AreEqual(1, writer.CallCount);
    }

    [TestMethod]
    public async Task SameText_DoesNotIncrementRevision()
    {
        var writer = new RecordingClipboardWriter();
        await using var coordinator = new StateCoordinator(writer);

        var first = await coordinator.SubmitRemoteAsync(Remote("same"));
        var second = await coordinator.SubmitRemoteAsync(Remote("same"));

        Assert.IsFalse(first.Deduplicated);
        Assert.IsTrue(second.Deduplicated);
        Assert.AreEqual(first.Snapshot, second.Snapshot);
        Assert.AreEqual(1L, coordinator.Revision);
        Assert.AreEqual(2U, coordinator.LastCommittedWindowsSequence);
        Assert.AreEqual(2, writer.CallCount);
    }

    [TestMethod]
    public async Task NewBoot_ChangesBootId()
    {
        await using var first = new StateCoordinator(new RecordingClipboardWriter());
        await using var second = new StateCoordinator(new RecordingClipboardWriter());

        Assert.AreNotEqual(Guid.Empty, first.BootId);
        Assert.AreNotEqual(Guid.Empty, second.BootId);
        Assert.AreNotEqual(first.BootId, second.BootId);
        Assert.AreEqual(0L, first.Revision);
        Assert.AreEqual(0L, second.Revision);
    }

    [TestMethod]
    public async Task LocalChannel_DropsIntermediateValues()
    {
        var writerEntered = NewSignal();
        var releaseWriter = NewSignal();
        var writer = new RecordingClipboardWriter(
            async (_, _, cancellationToken) =>
            {
                writerEntered.TrySetResult(true);
                await releaseWriter.Task.WaitAsync(cancellationToken);
                return new ClipboardWriteResult(10);
            });
        await using var coordinator = new StateCoordinator(writer);

        var remoteTask = coordinator.SubmitRemoteAsync(Remote("remote"));
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(coordinator.TryEnqueueLocal(Local(11, "A")));
        Assert.IsTrue(coordinator.TryEnqueueLocal(Local(12, "B")));
        Assert.IsTrue(coordinator.TryEnqueueLocal(Local(13, "C")));

        releaseWriter.TrySetResult(true);
        await remoteTask;
        await WaitUntilAsync(() => coordinator.LatestSnapshot?.WindowsSequence == 13);

        var latest = coordinator.LatestSnapshot;
        Assert.IsNotNull(latest);
        Assert.AreEqual("C", latest.Text);
        Assert.AreEqual(2L, latest.Revision);
        Assert.AreEqual(13U, latest.WindowsSequence);
    }

    [TestMethod]
    public async Task RemoteChannel_NeverDropsAcceptedCommand()
    {
        var writerEntered = NewSignal();
        var releaseFirstWrite = NewSignal();
        var writer = new RecordingClipboardWriter(
            async (call, _, cancellationToken) =>
            {
                if (call == 1)
                {
                    writerEntered.TrySetResult(true);
                    await releaseFirstWrite.Task.WaitAsync(cancellationToken);
                }

                return new ClipboardWriteResult((uint)call);
            });
        await using var coordinator = new StateCoordinator(writer);

        var submissions = new List<Task<RemoteClipboardResult>>
        {
            coordinator.SubmitRemoteAsync(Remote("remote-0")),
        };
        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var total = StateCoordinator.RemoteChannelCapacity + 4;
        for (var index = 1; index < total; index++)
        {
            submissions.Add(coordinator.SubmitRemoteAsync(Remote($"remote-{index}")));
        }

        releaseFirstWrite.TrySetResult(true);
        var results = await Task.WhenAll(submissions);

        Assert.AreEqual(total, writer.CallCount);
        Assert.HasCount(total, results);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, total).Select(value => (long)value).ToArray(),
            results.Select(result => result.Snapshot.Revision).Order().ToArray());
    }

    [TestMethod]
    public async Task ConcurrentProducers_AreSerializedWithContiguousRevisions()
    {
        const int producerCount = 32;
        var writer = new RecordingClipboardWriter(
            async (call, _, cancellationToken) =>
            {
                await Task.Delay(1, cancellationToken);
                return new ClipboardWriteResult((uint)call);
            });
        await using var coordinator = new StateCoordinator(writer);

        var submissions = Enumerable.Range(0, producerCount)
            .Select(index => Task.Run(
                () => coordinator.SubmitRemoteAsync(Remote($"value-{index}"))))
            .ToArray();
        var results = await Task.WhenAll(submissions);

        Assert.AreEqual(producerCount, writer.CallCount);
        Assert.AreEqual(1, writer.MaxConcurrentWrites);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, producerCount).Select(value => (long)value).ToArray(),
            results.Select(result => result.Snapshot.Revision).Order().ToArray());

        var finalResult = results.Single(result => result.Snapshot.Revision == producerCount);
        Assert.AreEqual(finalResult.Snapshot, coordinator.LatestSnapshot);
        Assert.AreEqual((uint)producerCount, coordinator.LastCommittedWindowsSequence);
    }

    [TestMethod]
    public async Task RemoteOriginEcho_DoesNotIncrementRevision()
    {
        var writer = new RecordingClipboardWriter(
            (_, _, _) => Task.FromResult(new ClipboardWriteResult(5)));
        await using var coordinator = new StateCoordinator(writer);

        var remote = await coordinator.SubmitRemoteAsync(Remote("remote"));
        var operationId = writer.Invocations.Single().OperationId;

        Assert.IsTrue(coordinator.TryEnqueueLocal(
            Local(6, "echo-with-newer-sequence", operationId: operationId)));
        await coordinator.DisposeAsync();

        Assert.AreEqual(remote.Snapshot, coordinator.LatestSnapshot);
        Assert.AreEqual(1L, coordinator.Revision);
        Assert.AreEqual(6U, coordinator.LastCommittedWindowsSequence);
    }

    [TestMethod]
    public async Task PrivateMarker_AdvancesSequenceWithoutPublishingText()
    {
        var startup = Local(1, "public");
        await using var coordinator = new StateCoordinator(
            new RecordingClipboardWriter(),
            startup);

        Assert.IsTrue(coordinator.TryEnqueueLocal(
            Local(2, "private-secret", isPrivate: true)));
        await coordinator.DisposeAsync();

        Assert.AreEqual(2U, coordinator.LastCommittedWindowsSequence);
        Assert.AreEqual(1L, coordinator.Revision);
        Assert.AreEqual("public", coordinator.LatestSnapshot?.Text);
        Assert.AreNotEqual("private-secret", coordinator.LatestSnapshot?.Text);
    }

    [TestMethod]
    public async Task StartupRehydrate_SeedsInitialSnapshotAndMetadata()
    {
        const string text = "启动 👋\nclipboard";
        var writer = new RecordingClipboardWriter();
        await using var coordinator = new StateCoordinator(writer, Local(7, text));

        var snapshot = coordinator.LatestSnapshot;
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(coordinator.BootId, snapshot.BootId);
        Assert.AreEqual(1L, snapshot.Revision);
        Assert.AreNotEqual(Guid.Empty, snapshot.ItemId);
        Assert.AreEqual(ClipboardOrigin.StartupRehydrate, snapshot.Origin);
        Assert.AreEqual(7U, snapshot.WindowsSequence);
        Assert.AreEqual(text, snapshot.Text);
        Assert.AreEqual(Hash(text), snapshot.Sha256);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(text), snapshot.Utf8Length);
        Assert.AreEqual(CapturedAt, snapshot.CapturedAtUtc);
        Assert.IsNull(snapshot.TailscaleUserLogin);
        Assert.IsTrue(coordinator.HasCommittedWindowsSequence);
        Assert.AreEqual(7U, coordinator.LastCommittedWindowsSequence);
        Assert.AreEqual(0, writer.CallCount);
    }

    [TestMethod]
    public async Task EqualOrOlderSequence_IsDiscardedAfterStartupRehydrate()
    {
        await using var coordinator = new StateCoordinator(
            new RecordingClipboardWriter(),
            Local(7, "startup"));

        Assert.IsTrue(coordinator.TryEnqueueLocal(Local(7, "equal")));
        await coordinator.DisposeAsync();

        Assert.AreEqual(1L, coordinator.Revision);
        Assert.AreEqual(7U, coordinator.LastCommittedWindowsSequence);
        Assert.AreEqual("startup", coordinator.LatestSnapshot?.Text);

        await using var olderCoordinator = new StateCoordinator(
            new RecordingClipboardWriter(),
            Local(7, "startup"));
        Assert.IsTrue(olderCoordinator.TryEnqueueLocal(Local(6, "older")));
        await olderCoordinator.DisposeAsync();

        Assert.AreEqual(1L, olderCoordinator.Revision);
        Assert.AreEqual(7U, olderCoordinator.LastCommittedWindowsSequence);
        Assert.AreEqual("startup", olderCoordinator.LatestSnapshot?.Text);
    }

    private static LocalClipboardCandidate Local(
        uint sequence,
        string text,
        bool isPrivate = false,
        Guid? operationId = null) =>
        new(sequence, text, CapturedAt, isPrivate, operationId);

    private static RemoteClipboardCommand Remote(string text) =>
        new(Guid.NewGuid(), text);

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new AssertFailedException("Timed out waiting for coordinator state.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class RecordingClipboardWriter : IClipboardWriter
    {
        private readonly object _sync = new();
        private readonly List<WriteInvocation> _invocations = [];
        private readonly Func<int, WriteInvocation, CancellationToken, Task<ClipboardWriteResult>>
            _writeAsync;
        private int _activeWrites;
        private int _callCount;
        private int _maxConcurrentWrites;

        public RecordingClipboardWriter(
            Func<int, WriteInvocation, CancellationToken, Task<ClipboardWriteResult>>?
                writeAsync = null)
        {
            _writeAsync = writeAsync
                ?? ((call, _, _) => Task.FromResult(new ClipboardWriteResult((uint)call)));
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaxConcurrentWrites => Volatile.Read(ref _maxConcurrentWrites);

        public IReadOnlyList<WriteInvocation> Invocations
        {
            get
            {
                lock (_sync)
                {
                    return [.. _invocations];
                }
            }
        }

        public async Task<ClipboardWriteResult> WriteTextAsync(
            string text,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            var invocation = new WriteInvocation(text, operationId);

            lock (_sync)
            {
                _invocations.Add(invocation);
                _activeWrites++;
                _maxConcurrentWrites = Math.Max(_maxConcurrentWrites, _activeWrites);
            }

            try
            {
                return await _writeAsync(call, invocation, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    _activeWrites--;
                }
            }
        }
    }

    private sealed record WriteInvocation(string Text, Guid OperationId);
}
