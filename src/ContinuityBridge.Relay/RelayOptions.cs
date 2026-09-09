using ContinuityBridge.Contracts;

namespace ContinuityBridge.Relay;

public sealed class RelayOptions
{
    public int RetentionSeconds { get; init; } = 1800;
    public int IdempotencyRetentionSeconds { get; init; } = 1800;
    public int IdempotencyMaxEntries { get; init; } = 512;
    public int MaxTextUtf8Bytes { get; init; } = 1_000_000;
    public int MaxTextJsonBytes { get; init; } = 8_000_000;
    public int MaxImageBytes { get; init; } = 20_000_000;
    public long MaxDecodedPixels { get; init; } = 64_000_000;
    public int MaxUploads { get; init; } = 2;
    public int MaxDownloads { get; init; } = 4;
    public int MaxWebSockets { get; init; } = 16;
    public int UploadTimeoutSeconds { get; init; } = 30;
    public int DownloadTimeoutSeconds { get; init; } = 30;
    public long MemoryBudgetBytes { get; init; } = 512_000_000;
    public string DeviceFile { get; init; } = "";

    public void Validate()
    {
        // Conservative payload budget includes JSON parser/string copies, PNG IDAT copy,
        // current body and worst-case distinct retired bodies held by bounded downloads.
        long largest = Math.Max(MaxImageBytes, MaxTextJsonBytes);
        long bound = MaxUploads * (6L * largest + 8L * MaxTextUtf8Bytes) +
            (MaxDownloads + 1L) * Math.Max(MaxImageBytes, MaxTextUtf8Bytes);
        if (RetentionSeconds is < 1 or > 86400 || IdempotencyRetentionSeconds < 1 ||
            IdempotencyRetentionSeconds > RetentionSeconds || IdempotencyMaxEntries is < 1 or > 10000 ||
            MaxTextUtf8Bytes is < 1 or > 16_000_000 || MaxTextJsonBytes < 6L * MaxTextUtf8Bytes + 128 ||
            MaxTextJsonBytes > 128_000_000 || MaxImageBytes is < 1 or > 128_000_000 ||
            MaxDecodedPixels is < 1 or > 64_000_000 || MaxUploads is < 1 or > 16 ||
            MaxDownloads is < 1 or > 32 || MaxWebSockets is < 1 or > 128 ||
            UploadTimeoutSeconds is < 1 or > 120 || DownloadTimeoutSeconds is < 1 or > 120 ||
            MemoryBudgetBytes < bound || string.IsNullOrWhiteSpace(DeviceFile))
            throw new InvalidOperationException("Invalid Relay configuration or payload memory budget.");
    }

    public Capabilities Capabilities() => new([1], ["text", "image"], ["image/png", "image/jpeg"],
        new(MaxTextUtf8Bytes, MaxTextJsonBytes, MaxImageBytes, MaxDecodedPixels), RetentionSeconds,
        new(IdempotencyRetentionSeconds, IdempotencyMaxEntries), ["none"], ["websocket-v1", "poll"], 4096, new());
}
