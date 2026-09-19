using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;

namespace ContinuityBridge.TestAgent;

// Local delivery format only. Import never creates a transport or desktop object.
// Deliberately not a record: generated ToString must not expose credentials.
internal sealed class StagingCredentials
{
    internal required string CandidateSha { get; init; }
    internal required string RunId { get; init; }
    internal required DateTimeOffset ExpiresAt { get; init; }
    internal required string QaOrigin { get; init; }
    internal required string RunnerToken { get; init; }
    internal required string RelayOrigin { get; init; }
    internal required string DeviceToken { get; init; }

    internal bool IsCurrent(string candidate, DateTimeOffset now) =>
        QaWire.IsSha(candidate) && CandidateSha == candidate && ExpiresAt > now && ExpiresAt - now <= TimeSpan.FromMinutes(45);

    public override string ToString() => "Staging credentials (redacted)";
}

internal static class StagingImport
{
    private const int MaxBytes = 8192;

    internal static bool TryLoad(string runnerPath, string relayPath, string candidate, DateTimeOffset now,
        out StagingCredentials? credentials)
    {
        credentials = null;
        byte[] runner = [], relay = [];
        try
        {
            runner = ReadRestricted(runnerPath);
            relay = ReadRestricted(relayPath);
            return TryParse(runner, relay, candidate, now, out credentials);
        }
        catch (Exception) { return false; } // No paths, parser excerpts, tokens or inner exceptions leave the loader.
        finally { CryptographicOperations.ZeroMemory(runner); CryptographicOperations.ZeroMemory(relay); }
    }

    internal static bool TryParse(ReadOnlyMemory<byte> runner, ReadOnlyMemory<byte> relay, string candidate,
        DateTimeOffset now, out StagingCredentials? credentials)
    {
        credentials = null;
        try
        {
            if (runner.Length is 0 or > MaxBytes || relay.Length is 0 or > MaxBytes) return false;
            using var qaDoc = JsonDocument.Parse(runner, new JsonDocumentOptions { MaxDepth = 4 });
            using var relayDoc = JsonDocument.Parse(relay, new JsonDocumentOptions { MaxDepth = 4 });
            var qa = Strings(qaDoc.RootElement, ["role", "runId", "candidateSha", "expiresAt", "token"]);
            var sync = Strings(relayDoc.RootElement,
                ["environment", "recipient", "runId", "candidateSha", "expiresAt", "qaOrigin", "relayOrigin", "deviceToken"]);
            if (qa["role"] != "runner" || sync["environment"] != "staging" || sync["recipient"] != "windows" ||
                !QaWire.IsId(qa["runId"]) || qa["runId"] != sync["runId"] || qa["candidateSha"] != sync["candidateSha"] ||
                !QaWire.IsHash(qa["token"]) || !QaWire.IsHash(sync["deviceToken"]) || qa["token"] == sync["deviceToken"]) return false;
            DateTimeOffset expiry = qaDoc.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
            DateTimeOffset relayExpiry = relayDoc.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
            if (!ExplicitUtc(qa["expiresAt"]) || !ExplicitUtc(sync["expiresAt"]) ||
                expiry.Offset != TimeSpan.Zero || relayExpiry.Offset != TimeSpan.Zero || expiry != relayExpiry) return false;
            var value = new StagingCredentials
            {
                CandidateSha = qa["candidateSha"], RunId = qa["runId"], ExpiresAt = expiry,
                QaOrigin = CloudTransport.ValidateOrigin(sync["qaOrigin"]).AbsoluteUri, RunnerToken = qa["token"],
                RelayOrigin = CloudTransport.ValidateOrigin(sync["relayOrigin"]).AbsoluteUri, DeviceToken = sync["deviceToken"],
            };
            if (!value.IsCurrent(candidate, now)) return false;
            credentials = value;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool ExplicitUtc(string value) =>
        value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal);

    private static Dictionary<string, string> Strings(JsonElement root, string[] allowed)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || property.Value.ValueKind != JsonValueKind.String ||
                !values.TryAdd(property.Name, property.Value.GetString()!)) throw new InvalidDataException();
        }
        if (values.Count != allowed.Length) throw new InvalidDataException();
        return values;
    }

    private static byte[] ReadRestricted(string path)
    {
        // Local fixed disks only; no UNC/network reads or reparse-point traversal.
        string full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal) ||
            full[2..].Contains(':', StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed) throw new InvalidDataException();
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        CheckAcl(new DirectoryInfo(Path.GetDirectoryName(full)!).GetAccessControl());
        using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Check the opened file's ACL, not just a pathname inspected before open.
        CheckAcl(file.GetAccessControl());
        if (file.Length is <= 0 or > MaxBytes) throw new InvalidDataException();
        byte[] bytes = new byte[(int)file.Length];
        try { file.ReadExactly(bytes); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static void CheckAcl(FileSystemSecurity acl)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidDataException();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!user.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new InvalidDataException();
        bool readable = false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (!user.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))) throw new InvalidDataException();
            if (user.Equals(rule.IdentityReference) && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & FileSystemRights.ReadData) != 0) readable = true;
        }
        if (!readable) throw new InvalidDataException();
    }
}
