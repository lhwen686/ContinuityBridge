using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Windows;

namespace ContinuityBridge.TestAgent;

internal interface IRunnerQa : IDisposable
{
    Task<QaSnapshot> PollAsync(QaPoll poll, CancellationToken cancellationToken);
    Task<QaSnapshot> ResultAsync(QaResult result, CancellationToken cancellationToken);
}

internal interface IRunnerDesktop : IAsyncDisposable
{
    Task PrepareAsync(CancellationToken cancellationToken);
    Task CaptureAsync(CancellationToken cancellationToken);
    Task<bool> SetAsync(string fixture, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string fixture, CancellationToken cancellationToken);
    Task<RestoreOutcome> RestoreAsync(CancellationToken cancellationToken);
}

internal interface IRunnerSync
{
    void Start(CancellationToken cancellationToken);
    Task StopAsync();
}

internal sealed record RunnerOutcome(string Reason, RestoreOutcome Restore, bool CleanupTimedOut);

// The only run orchestrator: UI, cancellation, expiry and exceptions use the same path.
internal sealed class RunnerSession(IRunnerQa qa, IRunnerDesktop desktop, IRunnerSync sync,
    string runId, string sha, string sessionId, Action<string>? status = null, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private int started;
    private DateTimeOffset? expiry;
    private long leaseStart;
    private TimeSpan leaseDuration;
    internal Task Quiesced { get; private set; } = Task.CompletedTask;
    internal TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan RestoreTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal async Task<RunnerOutcome> RunAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled) return new("未启用", RestoreOutcome.Untouched, false);
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("session_already_started");
        using var life = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        life.CancelAfter(TimeSpan.FromMinutes(45));
        Task operation = ExecuteAsync(life);
        string reason = "测试正常结束";
        try { await operation.WaitAsync(life.Token); }
        catch (OperationCanceledException) { reason = "用户停止、操作截止或租约到期"; }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("snapshot_", StringComparison.Ordinal))
        {
            reason = ex.Message switch
            {
                "snapshot_unsupported_format" => "快照被拒绝：含不支持的格式（文件、HTML/RTF、私有或 OLE 格式不备份）",
                "snapshot_format_limit" => "快照被拒绝：超过 16 种格式",
                _ => "快照被拒绝：文本/图片超过本机格式或总内存限额",
            };
        }
        catch (Exception) { reason = "配置、QA 身份/租约、网络、快照或 fixture 检查失败"; }
        await life.CancelAsync();
        // No restore until ALL command, preparation and sync work has stopped.
        Task drain = Task.WhenAll(IgnoreFailure(operation), StopSyncAsync());
        try { await drain.WaitAsync(DrainTimeout, CancellationToken.None); }
        catch (TimeoutException)
        {
            Quiesced = DisposeAfterAsync(drain);
            return new(reason, RestoreOutcome.TimedOut, true);
        }
        catch (Exception)
        {
            // A failed stop cannot establish quiescence. Never restore on that assumption.
            Quiesced = DisposeAfterAsync(IgnoreFailure(drain));
            return new("停止测试同步失败；已取消写入并跳过恢复", RestoreOutcome.Failed, true);
        }
        using var restoreDeadline = new CancellationTokenSource(RestoreTimeout);
        Task<RestoreOutcome> restore = desktop.RestoreAsync(restoreDeadline.Token);
        RestoreOutcome outcome;
        try { outcome = await restore.WaitAsync(RestoreTimeout, CancellationToken.None); }
        catch (TimeoutException)
        {
            await restoreDeadline.CancelAsync();
            Quiesced = DisposeAfterAsync(IgnoreFailure(restore));
            return new(reason, RestoreOutcome.TimedOut, true);
        }
        catch (Exception) { outcome = RestoreOutcome.Failed; }
        Quiesced = DisposeAfterAsync(Task.CompletedTask);
        try { await Quiesced.WaitAsync(DrainTimeout, CancellationToken.None); }
        catch (TimeoutException) { return new(reason, outcome, true); }
        return new(reason, outcome, false);
    }

    private async Task DisposeAfterAsync(Task work)
    {
        await work;
        try { await desktop.DisposeAsync(); }
        finally { qa.Dispose(); }
    }

    private static async Task IgnoreFailure(Task work)
    { try { await work; } catch (Exception) { /* Finite UI reason only; never exception/body logging. */ } }

    private async Task StopSyncAsync() => await sync.StopAsync();

    private async Task ExecuteAsync(CancellationTokenSource life)
    {
        var ct = life.Token;
        if (!QaWire.IsId(runId) || !QaWire.IsSha(sha) || !QaWire.IsId(sessionId)) throw new InvalidDataException();
        var poll = new QaPoll(runId, sha, sessionId);
        status?.Invoke("正在验证 QA 身份和短期租约；尚未访问剪贴板…");
        var snapshot = await qa.PollAsync(poll, ct);
        Validate(snapshot, allowEnded: false);
        life.CancelAfter(leaseDuration);
        await desktop.PrepareAsync(ct);
        CheckLease(ct);
        using (var snapshotDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            snapshotDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            using var abortRun = snapshotDeadline.Token.Register(life.Cancel);
            await desktop.CaptureAsync(snapshotDeadline.Token);
        }
        CheckLease(ct);
        if (!await desktop.SetAsync("sentinel-v1", ct)) throw new InvalidDataException();
        CheckLease(ct);
        sync.Start(ct);
        status?.Invoke("测试控制已启用 · runId=" + runId + " · 租约截止=" + expiry!.Value.ToString("O") + "；内存备份已建立。");
        var done = new Dictionary<string, (QaCommand Command, string Result)>();
        string? controller = null;
        while (true)
        {
            CheckLease(ct);
            if (snapshot.Ended) return;
            if (snapshot.Command is { } command && snapshot.Result is null)
            {
                if (command.RunId != runId || command.CandidateSha != sha || !QaWire.IsId(command.CommandId) ||
                    !QaWire.IsId(command.SessionId) || !QaWire.IsAction(command.Action, command.FixtureId)) throw new InvalidDataException();
                controller ??= command.SessionId;
                if (command.SessionId != controller) throw new InvalidDataException();
                string result;
                if (done.TryGetValue(command.CommandId, out var previous))
                {
                    if (previous.Command != command) throw new InvalidDataException();
                    result = previous.Result;
                }
                else
                {
                    if (done.Count >= 64) throw new InvalidDataException();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(90));
                    using var abortRun = deadline.Token.Register(life.Cancel);
                    result = command.Action switch
                    {
                        "EndRun" => "STOPPED",
                        "Status" => "PASS",
                        "SetFixtureClipboard" => await desktop.SetAsync(command.FixtureId!, deadline.Token) ? "PASS" : "MISMATCH",
                        "VerifyClipboard" => await desktop.VerifyAsync(command.FixtureId!, deadline.Token) ? "PASS" : "MISMATCH",
                        _ => throw new InvalidDataException(),
                    };
                    CheckLease(deadline.Token);
                    done.Add(command.CommandId, (command, result));
                }
                var reply = await qa.ResultAsync(new(runId, sha, sessionId, command.CommandId, result), ct);
                Validate(reply, allowEnded: true);
                if (reply.Command != command || reply.Result != result) throw new InvalidDataException();
                if (result != "PASS" || reply.Ended) return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
            snapshot = await qa.PollAsync(poll, ct);
            Validate(snapshot, allowEnded: true);
        }
    }

    private void Validate(QaSnapshot value, bool allowEnded)
    {
        if (value.Service != "ContinuityBridge.Qa.Sidecar" || value.Role != "runner" || value.SessionId != sessionId ||
            value.RunId != runId || value.CandidateSha != sha || !value.RunnerReady || value.Ended && !allowEnded ||
            value.Result is not (null or "PASS" or "MISMATCH" or "BLOCKED" or "STOPPED")) throw new InvalidDataException();
        if (expiry is null)
        {
            leaseDuration = value.ExpiresAt - clock.GetUtcNow();
            if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(45)) throw new InvalidDataException();
            expiry = value.ExpiresAt; leaseStart = clock.GetTimestamp();
        }
        if (value.ExpiresAt != expiry) throw new InvalidDataException();
        CheckLease(CancellationToken.None);
    }

    private void CheckLease(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (expiry is null || clock.GetUtcNow() >= expiry || clock.GetElapsedTime(leaseStart) >= leaseDuration)
            throw new InvalidDataException("lease_expired");
    }
}
