using System.Text.Json;

namespace ContinuityBridge.Contracts;

// Metadata only. Bodies deliberately cannot occur anywhere in State or Receipt.
public sealed record Item(string ItemId, string Kind, string MimeType, int ByteLength,
    string Sha256, string SourceDeviceId, string CryptoMode, DateTime CommittedAt, DateTime ExpiresAt);
public sealed record State(int ProtocolVersion, string ServerEpoch, string Revision, string Etag, Item? Item);
public sealed record MutationReceipt(string RequestId, string Operation, State Result, State State,
    bool Replayed, bool Available);
public sealed record ChangeEvent(string Type, string ServerEpoch, string Revision);
public sealed record DeviceIdentityResponse(string DeviceId);
public sealed record Limits(int MaxTextUtf8Bytes, int MaxTextJsonBytes, int MaxImageBytes, long MaxDecodedPixels);
public sealed record IdempotencyCapability(int RetentionSeconds, int MaxEntries, string Scope = "device", bool StoresBodies = false);
public sealed record StorageCapability(string Mode = "memory", bool SurvivesRestart = false);
public sealed record Capabilities(int[] ProtocolVersions, string[] Kinds, string[] ImageMimeTypes,
    Limits Limits, int RetentionSeconds, IdempotencyCapability Idempotency, string[] CryptoModes,
    string[] Notifications, int MaxEventBytes, StorageCapability Storage);

public static class Wire
{
    public const int Version = 1;
    public const string CryptoMode = "none";
    // A future envelope is a new versioned contract, never an optional plaintext fallback.
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}
