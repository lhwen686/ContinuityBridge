using ContinuityBridge.Core;

namespace ContinuityBridge.TestAgent;

// Same product engine, fixture-only desktop, ephemeral staging credentials.
// Never reads/saves AppSettings, Credential Manager entries or logon settings.
internal sealed class RunnerSync(ICloudClipboard clipboard, string? origin, string? token, Action stop) : IRunnerSync, IAsyncDisposable
{
    private CloudTransport? transport;
    private CancellationTokenSource? life;
    private Task? task;
    private Task? stopped;
    private readonly object gate = new();
    private bool stopping;
    public void Start(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (stopping) throw new OperationCanceledException("test_sync_stopped", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (origin is null || token is null) return;
            transport = new(origin, token);
            life = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var engine = new CloudSyncEngine(clipboard, transport);
            engine.StatusChanged += value =>
            {
                if (value is SyncStatus.AuthenticationRequired or SyncStatus.InvalidContent or SyncStatus.Offline) stop();
            };
            task = ObserveAsync(engine, life.Token);
        }
    }
    private async Task ObserveAsync(CloudSyncEngine engine, CancellationToken ct)
    { try { await engine.RunAsync(ct); } catch (Exception) { stop(); } }
    public Task StopAsync()
    { lock (gate) { stopping = true; return stopped ??= StopCoreAsync(); } }
    private async Task StopCoreAsync()
    {
        if (life is not null) await life.CancelAsync();
        if (task is not null) await task;
        transport?.Dispose(); life?.Dispose();
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
