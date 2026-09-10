using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContinuityBridge.Qa.Protocol;

public sealed record QaLease(string RunId, string CandidateSha, DateTimeOffset ExpiresAt, string ControllerTokenHash, string RunnerTokenHash);
public sealed record QaCommand(string RunId, string CandidateSha, string SessionId, string CommandId, string Action, string? FixtureId);
public sealed record QaPoll(string RunId, string CandidateSha, string SessionId);
public sealed record QaResult(string RunId, string CandidateSha, string SessionId, string CommandId, string Result);
public sealed record QaSnapshot(string RunId, string CandidateSha, DateTimeOffset ExpiresAt, bool RunnerReady, bool Ended, QaCommand? Command, string? Result);

public static class QaWire
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    public static bool IsSha(string? value) => value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsId(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsAction(string? action, string? fixture) => action switch
    {
        "SetFixtureClipboard" or "VerifyClipboard" => FixtureCatalog.Ids.Contains(fixture, StringComparer.Ordinal),
        "Status" or "EndRun" => fixture is null,
        _ => false
    };
}
