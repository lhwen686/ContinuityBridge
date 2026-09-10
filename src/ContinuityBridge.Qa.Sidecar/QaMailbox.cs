using System.Security.Cryptography;
using System.Text;
using ContinuityBridge.Qa.Protocol;

namespace ContinuityBridge.Qa.Sidecar;

public sealed class QaRequestException(int status) : Exception("qa_request_rejected") { public int Status { get; } = status; }

public sealed class QaMailbox
{
    private readonly object gate = new();
    private readonly QaLease lease;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly TimeSpan duration;
    private string? runner;
    private string? controller;
    private QaCommand? command;
    private string? result;
    private long commandStarted;
    private bool ended;
    private readonly Dictionary<string, (QaCommand Command, string? Result)> history = [];

    public QaMailbox(QaLease lease, string buildSha, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System; this.lease = lease;
        duration = lease.ExpiresAt - this.clock.GetUtcNow(); started = this.clock.GetTimestamp();
        if (!QaWire.IsId(lease.RunId) || !QaWire.IsSha(buildSha) || lease.CandidateSha != buildSha ||
            !QaWire.IsHash(lease.ControllerTokenHash) || !QaWire.IsHash(lease.RunnerTokenHash) ||
            lease.ControllerTokenHash == lease.RunnerTokenHash || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(45))
            throw new InvalidDataException("invalid_qa_lease");
    }

    public void Authenticate(string authorization, bool isRunner)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !QaWire.IsHash(authorization[7..])) throw new QaRequestException(401);
        byte[] actual = SHA256.HashData(Encoding.ASCII.GetBytes(authorization[7..]));
        byte[] expected = Convert.FromHexString(isRunner ? lease.RunnerTokenHash : lease.ControllerTokenHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new QaRequestException(403);
        lock (gate) CheckLease();
    }

    public QaSnapshot Submit(QaCommand request)
    {
        lock (gate)
        {
            Check(request.RunId, request.CandidateSha, request.SessionId);
            if (!QaWire.IsId(request.CommandId) || !QaWire.IsAction(request.Action, request.FixtureId)) throw new QaRequestException(400);
            if (controller is not null && controller != request.SessionId) throw new QaRequestException(409);
            controller ??= request.SessionId;
            if (history.TryGetValue(request.CommandId, out var previous))
            {
                if (previous.Command != request) throw new QaRequestException(409);
                return Snapshot(previous.Command, previous.Result);
            }
            if (ended || command is not null && result is null || history.Count >= 64) throw new QaRequestException(409);
            command = request; result = null; commandStarted = clock.GetTimestamp(); history.Add(request.CommandId, (request, null));
            return Snapshot(command, result);
        }
    }

    public QaSnapshot Poll(QaPoll request)
    {
        lock (gate)
        {
            Check(request.RunId, request.CandidateSha, request.SessionId);
            if (runner is not null && runner != request.SessionId) throw new QaRequestException(409);
            runner ??= request.SessionId;
            return Snapshot(command, result);
        }
    }

    public QaSnapshot Acknowledge(QaResult request)
    {
        lock (gate)
        {
            Check(request.RunId, request.CandidateSha, request.SessionId);
            if (runner != request.SessionId || command?.CommandId != request.CommandId) throw new QaRequestException(409);
            if (request.Result is not ("PASS" or "MISMATCH" or "BLOCKED" or "STOPPED")) throw new QaRequestException(400);
            if (result is not null && result != request.Result) throw new QaRequestException(409);
            result = request.Result; history[command.CommandId] = (command, result);
            if (command.Action == "EndRun" || result is "MISMATCH" or "BLOCKED" or "STOPPED") ended = true;
            return Snapshot(command, result);
        }
    }

    public QaSnapshot Status()
    { lock (gate) { CheckLease(); return Snapshot(command, result); } }

    private void Check(string runId, string sha, string sessionId)
    {
        CheckLease();
        if (runId != lease.RunId || sha != lease.CandidateSha || !QaWire.IsId(sessionId)) throw new QaRequestException(409);
    }
    private void CheckLease()
    {
        if (clock.GetUtcNow() >= lease.ExpiresAt || clock.GetElapsedTime(started) >= duration)
        { ended = true; history.Clear(); command = null; result = null; throw new QaRequestException(410); }
        if (command is not null && result is null && clock.GetElapsedTime(commandStarted) >= TimeSpan.FromSeconds(120))
        { ended = true; result = "BLOCKED"; history[command.CommandId] = (command, result); }
    }
    private QaSnapshot Snapshot(QaCommand? value, string? outcome) => new(lease.RunId, lease.CandidateSha, lease.ExpiresAt, runner is not null, ended, value, outcome);
}
