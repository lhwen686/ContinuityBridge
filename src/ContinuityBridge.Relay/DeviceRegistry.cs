using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContinuityBridge.Contracts;

namespace ContinuityBridge.Relay;

public sealed record DeviceRegistration(string DeviceId, string TokenSha256, bool Revoked = false);
public sealed record DeviceIdentity(string DeviceId, string TokenHash);

public sealed class DeviceRegistry(string path)
{
    private readonly object gate = new();
    private Dictionary<string, string> active = [];

    // Bounded file reload; a missing, partial or invalid update fails closed. Operator replaces
    // the file atomically. No secret-containing configuration or error is ever logged.
    public void Reload()
    {
        lock (gate)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 65536) throw new InvalidDataException();
                byte[] bytes = new byte[checked((int)file.Length)];
                file.ReadExactly(bytes);
                var entries = JsonSerializer.Deserialize<DeviceRegistration[]>(bytes, Wire.Json) ?? throw new InvalidDataException();
                if (entries.Length is < 1 or > 128) throw new InvalidDataException();
                var next = new Dictionary<string, string>(StringComparer.Ordinal);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (!RequestValidation.IsIdentifier(entry.DeviceId) || !ids.Add(entry.DeviceId) ||
                        entry.TokenSha256.Length != 64 || entry.TokenSha256.Any(c => !char.IsAsciiHexDigit(c)))
                        throw new InvalidDataException();
                    if (!entry.Revoked) next.Add(entry.TokenSha256.ToLowerInvariant(), entry.DeviceId);
                }
                active = next;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or NullReferenceException)
            { active = []; }
        }
    }

    public DeviceIdentity Authenticate(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new ProtocolException(401, "unauthorized");
        string token = authorization[7..];
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw new ProtocolException(401, "unauthorized");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
        Reload();
        lock (gate)
        {
            if (!active.TryGetValue(hash, out var id)) throw new ProtocolException(401, "unauthorized");
            return new(id, hash);
        }
    }

    public bool IsActive(DeviceIdentity identity)
    {
        lock (gate) return active.TryGetValue(identity.TokenHash, out var id) && id == identity.DeviceId;
    }

    public static void Provision(string directory)
    {
        Directory.CreateDirectory(directory);
        string registry = Path.Combine(directory, "devices.json");
        string credentials = Path.Combine(directory, "client-tokens.json");
        if (File.Exists(registry) || File.Exists(credentials)) throw new InvalidOperationException("Provisioning output already exists.");
        var clients = Enumerable.Range(0, 2).Select(_ => new
        {
            deviceId = Guid.NewGuid().ToString("N"),
            token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))
        }).ToArray();
        WritePrivate(credentials, JsonSerializer.SerializeToUtf8Bytes(clients, Wire.Json));
        WritePrivate(registry, JsonSerializer.SerializeToUtf8Bytes(clients.Select(c => new DeviceRegistration(c.deviceId,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(c.token))))), Wire.Json));
    }

    private static void WritePrivate(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(bytes);
    }
}
