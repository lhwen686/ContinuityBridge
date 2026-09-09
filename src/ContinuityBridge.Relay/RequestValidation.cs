using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContinuityBridge.Relay;

public static class RequestValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static bool IsIdentifier(string? value) => value is { Length: >= 1 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static (string Etag, Guid Key) Conditions(HttpRequest request)
    {
        string etag = request.Headers.IfMatch.ToString();
        if (etag.Length == 0) throw new ProtocolException(428, "precondition_required");
        if (etag.Length < 3 || etag[0] != '"' || etag[^1] != '"' || !IsIdentifier(etag[1..^1]))
            throw new ProtocolException(400, "invalid_request");
        if (!Guid.TryParseExact(request.Headers["Idempotency-Key"], "D", out var key) || key == Guid.Empty)
            throw new ProtocolException(400, "invalid_request");
        return (etag, key);
    }

    public static async Task<byte[]> ReadBoundedAsync(HttpRequest request, int limit, CancellationToken token)
    {
        if (request.ContentLength > limit) throw new ProtocolException(413, "payload_too_large");
        // One admitted fixed-size buffer, plus the exact owned result. No disk spooling,
        // array-pool history or unbounded MemoryStream growth, including chunked requests.
        byte[] buffer = new byte[limit + 1];
        int length = 0;
        while (true)
        {
            int read = await request.Body.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (length > limit) throw new ProtocolException(413, "payload_too_large");
        }
        token.ThrowIfCancellationRequested();
        return buffer.AsSpan(0, length).ToArray();
    }

    public static byte[] Text(byte[] json, RelayOptions options)
    {
        try
        {
            // GetString() on JSON strings can replace invalid surrogate pairs. Validate
            // the raw escape sequence first, as well as the wire UTF-8 itself.
            string wire = StrictUtf8.GetString(json);
            ValidateEscapes(wire);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            string? text = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String) throw new JsonException();
                if (property.Name == "text") text = property.Value.GetString();
                else if (property.Name == "cryptoMode")
                {
                    if (property.Value.GetString() != "none") throw new ProtocolException(415, "unsupported_crypto_mode");
                }
                else throw new JsonException();
            }
            if (text is null || text.Contains('\0', StringComparison.Ordinal)) throw new JsonException();
            byte[] body = StrictUtf8.GetBytes(text);
            if (body.Length > options.MaxTextUtf8Bytes) throw new ProtocolException(413, "payload_too_large");
            return body;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or EncoderFallbackException or FormatException)
        { throw new ProtocolException(400, "invalid_request"); }
    }

    private static void ValidateEscapes(string wire)
    {
        for (int i = 0; i < wire.Length; i++)
        {
            if (wire[i] != '\\') continue;
            if (++i >= wire.Length) throw new JsonException();
            if (wire[i] != 'u') continue;
            if (i + 4 >= wire.Length) throw new JsonException();
            int value = Convert.ToInt32(wire.Substring(i + 1, 4), 16);
            i += 4;
            if (value is >= 0xdc00 and <= 0xdfff) throw new JsonException();
            if (value is < 0xd800 or > 0xdbff) continue;
            if (i + 6 >= wire.Length || wire[i + 1] != '\\' || wire[i + 2] != 'u') throw new JsonException();
            int low = Convert.ToInt32(wire.Substring(i + 3, 4), 16);
            if (low is < 0xdc00 or > 0xdfff) throw new JsonException();
            i += 6;
        }
    }

    public static string Fingerprint(string method, string path, string mime, string mode, string etag, byte[]? body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string part in new[] { method, path, mime, mode, etag }) Append(hash, StrictUtf8.GetBytes(part));
        Append(hash, body ?? []);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
