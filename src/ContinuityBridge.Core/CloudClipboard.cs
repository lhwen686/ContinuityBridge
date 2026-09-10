using System.Security.Cryptography;
using System.Text;
using ContinuityBridge.Contracts;

namespace ContinuityBridge.Core;

// Product v1 uses these types, not the historical Tailnet StateCoordinator.
// Bodies live only in the active capture/transfer, never in a history or settings file.
public sealed record ClipboardPayload(string Kind, string MimeType, byte[] Bytes)
{
    public static UTF8Encoding StrictUtf8 { get; } = new(false, true);
    public static ClipboardPayload Text(string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal)) throw new InvalidDataException("text_nul");
        return new("text", "text/plain; charset=utf-8", StrictUtf8.GetBytes(value));
    }

    public void Validate(Limits limits)
    {
        if (Kind == "text" && MimeType == "text/plain; charset=utf-8")
        {
            if (Bytes.Length > limits.MaxTextUtf8Bytes) throw new InvalidDataException("text_limit");
            if (StrictUtf8.GetString(Bytes).Contains('\0', StringComparison.Ordinal)) throw new InvalidDataException("text_nul");
        }
        else if (Kind == "image" && MimeType is "image/png" or "image/jpeg")
        {
            if (Bytes.Length == 0 || Bytes.Length > limits.MaxImageBytes) throw new InvalidDataException("image_limit");
        }
        else throw new InvalidDataException("unsupported_type");
    }

    public bool Matches(Item item) => Kind == item.Kind && MimeType == item.MimeType &&
        Bytes.Length == item.ByteLength && Convert.ToHexStringLower(SHA256.HashData(Bytes)) == item.Sha256;
}

public interface ICloudClipboard : IAsyncDisposable
{
    long Generation { get; }
    event Action<long>? Changed;
    Task StartAsync(CancellationToken cancellationToken);
    Task<ClipboardPayload?> CaptureAsync(long expected, Limits limits, CancellationToken cancellationToken);
    Task<bool> TryApplyAsync(ClipboardPayload payload, long expected, Limits limits, CancellationToken cancellationToken);
}

public interface ICloudTransport : IDisposable
{
    Task<Capabilities> CapabilitiesAsync(CancellationToken cancellationToken);
    Task<string> IdentityAsync(CancellationToken cancellationToken);
    Task<State> StateAsync(CancellationToken cancellationToken);
    Task<MutationReceipt> PutAsync(ClipboardPayload payload, string etag, Guid key, CancellationToken cancellationToken);
    Task<MutationReceipt> ClearAsync(string etag, Guid key, CancellationToken cancellationToken);
    Task<ClipboardPayload> DownloadAsync(Item item, Limits limits, CancellationToken cancellationToken);
    Task WatchAsync(Action changed, Action<bool> connected, CancellationToken cancellationToken);
}

public sealed class CloudRequestException(int statusCode) : Exception("relay_request_failed")
{
    public int StatusCode { get; } = statusCode;
}

// The DWORD is change evidence, never an ordered clock. Re-read the live DWORD
// on notification; repeated/delayed messages then collapse without a half-range assumption.
public sealed class ClipboardSequence
{
    public uint Current { get; private set; }
    public long Generation { get; private set; }
    public void Baseline(uint current) => Current = current;
    public bool Observe(uint current)
    {
        if (current == Current) return false;
        Current = current;
        Generation = checked(Generation + 1);
        return true;
    }
}
