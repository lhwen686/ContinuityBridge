using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace ContinuityBridge.Core;

public sealed class StateCoordinator : IAsyncDisposable
{
    public const int RemoteChannelCapacity = 16;
    public const int LocalChannelCapacity = 1;
    public const int IdempotencyCapacity = 512;

    public static readonly TimeSpan RemoteEnqueueTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);

    private readonly IClipboardWriter _clipboardWriter;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<RemoteEnvelope> _remoteChannel;
    private readonly Channel<LocalClipboardCandidate> _localChannel;
    private readonly Dictionary<Guid, LinkedListNode<IdempotencyEntry>> _idempotencyByRequestId = [];
    private readonly LinkedList<IdempotencyEntry> _idempotencyLru = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _processingTask;

    private ClipboardSnapshot? _latestSnapshot;
    private long _revision;
    private uint _lastCommittedWindowsSequence;
    private Guid _lastRemoteOperationId;
    private int _hasCommittedWindowsSequence;
    private int _hasRemoteOperationId;
    private int _disposeStarted;

    public StateCoordinator(
        IClipboardWriter clipboardWriter,
        LocalClipboardCandidate? startupClipboard = null,
        TimeProvider? timeProvider = null)
    {
        _clipboardWriter = clipboardWriter ?? throw new ArgumentNullException(nameof(clipboardWriter));
        _timeProvider = timeProvider ?? TimeProvider.System;
        BootId = Guid.NewGuid();

        _remoteChannel = Channel.CreateBounded<RemoteEnvelope>(
            new BoundedChannelOptions(RemoteChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

        _localChannel = Channel.CreateBounded<LocalClipboardCandidate>(
            new BoundedChannelOptions(LocalChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false,
            });

        if (startupClipboard is not null)
        {
            ProcessLocalCandidate(startupClipboard, ClipboardOrigin.StartupRehydrate);
        }

        _processingTask = ProcessLoopAsync(_shutdown.Token);
    }

    public Guid BootId { get; }

    public ClipboardSnapshot? LatestSnapshot => Volatile.Read(ref _latestSnapshot);

    public long Revision => Volatile.Read(ref _revision);

    public bool HasCommittedWindowsSequence => Volatile.Read(ref _hasCommittedWindowsSequence) != 0;

    public uint LastCommittedWindowsSequence => Volatile.Read(ref _lastCommittedWindowsSequence);

    public Task Completion => _processingTask;

    public bool TryEnqueueLocal(LocalClipboardCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ThrowIfDisposing();
        return _localChannel.Writer.TryWrite(candidate);
    }

    public async Task<RemoteClipboardResult> SubmitRemoteAsync(
        RemoteClipboardCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Text);

        if (command.RequestId == Guid.Empty)
        {
            throw new ArgumentException("RequestId must be a non-empty UUID.", nameof(command));
        }

        ThrowIfDisposing();

        var completion = new TaskCompletionSource<RemoteClipboardResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var envelope = new RemoteEnvelope(command, completion);

        using var enqueueTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        enqueueTimeout.CancelAfter(RemoteEnqueueTimeout);

        try
        {
            await _remoteChannel.Writer.WriteAsync(envelope, enqueueTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoordinatorBusyException("The coordinator remote queue remained full for two seconds.");
        }
        catch (ChannelClosedException exception)
        {
            throw new ObjectDisposedException(nameof(StateCoordinator), exception);
        }

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _processingTask.ConfigureAwait(false);
            return;
        }

        _remoteChannel.Writer.TryComplete();
        _localChannel.Writer.TryComplete();

        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var processedWork = false;

                if (_remoteChannel.Reader.TryRead(out var remote))
                {
                    processedWork = true;
                    await ProcessRemoteEnvelopeAsync(remote, cancellationToken).ConfigureAwait(false);
                }

                if (_localChannel.Reader.TryRead(out var local))
                {
                    processedWork = true;
                    ProcessLocalCandidate(local, ClipboardOrigin.WindowsLocal);
                }

                if (processedWork)
                {
                    continue;
                }

                var remoteReady = _remoteChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var localReady = _localChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(remoteReady, localReady).ConfigureAwait(false);

                if (!await completed.ConfigureAwait(false)
                    && _remoteChannel.Reader.Completion.IsCompleted
                    && _localChannel.Reader.Completion.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            FailPendingRemoteCommands();
        }
    }

    private async Task ProcessRemoteEnvelopeAsync(
        RemoteEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRemoteCommandAsync(envelope.Command, cancellationToken)
                .ConfigureAwait(false);
            envelope.Completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            envelope.Completion.TrySetException(exception);
        }
    }

    private async Task<RemoteClipboardResult> ProcessRemoteCommandAsync(
        RemoteClipboardCommand command,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        RemoveExpiredIdempotencyEntries(now);

        if (_idempotencyByRequestId.TryGetValue(command.RequestId, out var existingNode))
        {
            TouchIdempotencyEntry(existingNode);
            return existingNode.Value.Result;
        }

        var (sha256, utf8Length) = ComputeTextMetadata(command.Text);
        var latest = _latestSnapshot;
        var isSameContent = latest is not null
            && StringComparer.Ordinal.Equals(latest.Sha256, sha256);
        var operationId = Guid.NewGuid();

        var writeResult = await _clipboardWriter
            .WriteTextAsync(command.Text, operationId, cancellationToken)
            .ConfigureAwait(false);

        if (HasCommittedWindowsSequence
            && writeResult.WindowsSequence <= _lastCommittedWindowsSequence)
        {
            throw new InvalidOperationException(
                "The clipboard writer returned a sequence that was not newer than the committed sequence.");
        }

        CommitWindowsSequence(writeResult.WindowsSequence);
        _lastRemoteOperationId = operationId;
        Volatile.Write(ref _hasRemoteOperationId, 1);

        if (isSameContent)
        {
            var deduplicated = new RemoteClipboardResult(latest!, Deduplicated: true);
            AddIdempotencyEntry(command.RequestId, now, deduplicated);
            return deduplicated;
        }

        var snapshot = new ClipboardSnapshot(
            BootId,
            NextRevision(),
            operationId,
            ClipboardOrigin.IPhoneRemote,
            writeResult.WindowsSequence,
            command.Text,
            sha256,
            utf8Length,
            _timeProvider.GetUtcNow(),
            command.TailscaleUserLogin);

        Volatile.Write(ref _latestSnapshot, snapshot);

        var result = new RemoteClipboardResult(snapshot, Deduplicated: false);
        AddIdempotencyEntry(command.RequestId, now, result);
        return result;
    }

    private void ProcessLocalCandidate(
        LocalClipboardCandidate candidate,
        ClipboardOrigin origin)
    {
        if (HasCommittedWindowsSequence
            && candidate.WindowsSequence <= _lastCommittedWindowsSequence)
        {
            return;
        }

        CommitWindowsSequence(candidate.WindowsSequence);

        if (candidate.IsPrivate)
        {
            return;
        }

        var latest = _latestSnapshot;

        if (candidate.OriginOperationId is Guid operationId
            && Volatile.Read(ref _hasRemoteOperationId) != 0
            && _lastRemoteOperationId == operationId)
        {
            return;
        }

        if (candidate.Text is null)
        {
            return;
        }

        var (sha256, utf8Length) = ComputeTextMetadata(candidate.Text);

        if (latest is not null && StringComparer.Ordinal.Equals(latest.Sha256, sha256))
        {
            return;
        }

        var snapshot = new ClipboardSnapshot(
            BootId,
            NextRevision(),
            Guid.NewGuid(),
            origin,
            candidate.WindowsSequence,
            candidate.Text,
            sha256,
            utf8Length,
            candidate.CapturedAtUtc,
            TailscaleUserLogin: null);

        Volatile.Write(ref _latestSnapshot, snapshot);
    }

    private static (string Sha256, int Utf8Length) ComputeTextMetadata(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(utf8);
        return (Convert.ToHexStringLower(hash), utf8.Length);
    }

    private long NextRevision() => Interlocked.Increment(ref _revision);

    private void CommitWindowsSequence(uint windowsSequence)
    {
        Volatile.Write(ref _lastCommittedWindowsSequence, windowsSequence);
        Volatile.Write(ref _hasCommittedWindowsSequence, 1);
    }

    private void AddIdempotencyEntry(
        Guid requestId,
        DateTimeOffset createdAtUtc,
        RemoteClipboardResult result)
    {
        var entry = new IdempotencyEntry(requestId, createdAtUtc, result);
        var node = _idempotencyLru.AddFirst(entry);
        _idempotencyByRequestId.Add(requestId, node);

        while (_idempotencyByRequestId.Count > IdempotencyCapacity)
        {
            RemoveLeastRecentlyUsedIdempotencyEntry();
        }
    }

    private void TouchIdempotencyEntry(LinkedListNode<IdempotencyEntry> node)
    {
        _idempotencyLru.Remove(node);
        _idempotencyLru.AddFirst(node);
    }

    private void RemoveExpiredIdempotencyEntries(DateTimeOffset now)
    {
        var node = _idempotencyLru.First;

        while (node is not null)
        {
            var next = node.Next;

            if (now - node.Value.CreatedAtUtc >= IdempotencyRetention)
            {
                RemoveIdempotencyEntry(node);
            }

            node = next;
        }
    }

    private void RemoveLeastRecentlyUsedIdempotencyEntry()
    {
        if (_idempotencyLru.Last is { } node)
        {
            RemoveIdempotencyEntry(node);
        }
    }

    private void RemoveIdempotencyEntry(LinkedListNode<IdempotencyEntry> node)
    {
        _idempotencyLru.Remove(node);
        _idempotencyByRequestId.Remove(node.Value.RequestId);
    }

    private void FailPendingRemoteCommands()
    {
        var exception = new ObjectDisposedException(nameof(StateCoordinator));

        while (_remoteChannel.Reader.TryRead(out var envelope))
        {
            envelope.Completion.TrySetException(exception);
        }
    }

    private void ThrowIfDisposing()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
    }

    private sealed record RemoteEnvelope(
        RemoteClipboardCommand Command,
        TaskCompletionSource<RemoteClipboardResult> Completion);

    private sealed record IdempotencyEntry(
        Guid RequestId,
        DateTimeOffset CreatedAtUtc,
        RemoteClipboardResult Result);
}

public sealed class CoordinatorBusyException : Exception
{
    public CoordinatorBusyException(string message)
        : base(message)
    {
    }
}
