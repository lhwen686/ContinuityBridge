using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContinuityBridge.Contracts;

namespace ContinuityBridge.Core;

public sealed partial class CloudTransport : ICloudTransport
{
    private readonly HttpClient client;
    private readonly Uri origin;
    private readonly string token;
    private static readonly JsonSerializerOptions ClientJson = new(Wire.Json)
        { RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 8 };

    public CloudTransport(string baseUrl, string deviceToken)
        : this(baseUrl, deviceToken, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    // Handler injection is for process tests with an ephemeral, pinned TLS certificate.
    // The shipping constructor always uses normal OS trust validation.
    public CloudTransport(string baseUrl, string deviceToken, HttpMessageHandler handler)
    {
        origin = ValidateOrigin(baseUrl);
        if (deviceToken.Length != 64 || !deviceToken.All(char.IsAsciiHexDigit)) throw new ArgumentException("invalid_device_token");
        token = deviceToken;
        client = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static Uri ValidateOrigin(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var value) || value.Scheme != "https" ||
            value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0 || value.AbsolutePath != "/")
            throw new ArgumentException("HTTPS_origin_required");
        return value;
    }

    public async Task<Capabilities> CapabilitiesAsync(CancellationToken cancellationToken)
    {
        var caps = await JsonAsync<Capabilities>(new(HttpMethod.Get, "/v1/capabilities"), cancellationToken).ConfigureAwait(false);
        if (!caps.ProtocolVersions.Contains(1) || !caps.CryptoModes.Contains("none") ||
            caps.Limits.MaxTextUtf8Bytes is < 1 or > 8_000_000 || caps.Limits.MaxImageBytes is < 1 or > 40_000_000 ||
            caps.Limits.MaxDecodedPixels < 1 || caps.MaxEventBytes is < 1 or > 4096)
            throw new InvalidDataException("incompatible_capabilities");
        return caps;
    }

    public async Task<string> IdentityAsync(CancellationToken cancellationToken)
    {
        var identity = await JsonAsync<DeviceIdentityResponse>(new(HttpMethod.Get, "/v1/identity"), cancellationToken).ConfigureAwait(false);
        if (!Identifier().IsMatch(identity.DeviceId)) throw new InvalidDataException("invalid_identity");
        return identity.DeviceId;
    }

    public async Task<State> StateAsync(CancellationToken cancellationToken)
    {
        var state = await JsonAsync<State>(new(HttpMethod.Get, "/v1/clipboard"), cancellationToken).ConfigureAwait(false);
        ValidateState(state);
        return state;
    }

    public Task<MutationReceipt> PutAsync(ClipboardPayload payload, string etag, Guid key, CancellationToken cancellationToken)
    {
        var request = Mutation(HttpMethod.Post, "/v1/items/" + payload.Kind, etag, key);
        request.Content = payload.Kind == "text"
            ? new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { text = ClipboardPayload.StrictUtf8.GetString(payload.Bytes) }, Wire.Json))
            : new ByteArrayContent(payload.Bytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(payload.Kind == "text" ? "application/json" : payload.MimeType);
        return ReceiptAsync(request, key, cancellationToken);
    }

    public Task<MutationReceipt> ClearAsync(string etag, Guid key, CancellationToken cancellationToken) =>
        ReceiptAsync(Mutation(HttpMethod.Delete, "/v1/clipboard", etag, key), key, cancellationToken);

    private async Task<MutationReceipt> ReceiptAsync(HttpRequestMessage request, Guid key, CancellationToken cancellationToken)
    {
        var result = await JsonAsync<MutationReceipt>(request, cancellationToken).ConfigureAwait(false);
        if (result.RequestId != key.ToString()) throw new InvalidDataException("invalid_receipt");
        ValidateState(result.State); ValidateState(result.Result);
        return result;
    }

    public async Task<ClipboardPayload> DownloadAsync(Item item, Limits limits, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40)); cancellationToken = deadline.Token;
        if (!Identifier().IsMatch(item.ItemId) || item.CryptoMode != "none") throw new InvalidDataException("invalid_item");
        int limit = item.Kind == "text" ? limits.MaxTextUtf8Bytes : limits.MaxImageBytes;
        if (item.ByteLength < 0 || item.ByteLength > limit) throw new InvalidDataException("item_limit");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/items/" + item.ItemId + "/content");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        RequireSuccess(response);
        if (response.Content.Headers.ContentType?.ToString() != item.MimeType ||
            response.Content.Headers.ContentLength is long length && length != item.ByteLength)
            throw new InvalidDataException("content_metadata_mismatch");
        var body = await ReadBoundedAsync(response.Content, item.ByteLength, cancellationToken).ConfigureAwait(false);
        var payload = new ClipboardPayload(item.Kind, item.MimeType, body);
        payload.Validate(limits);
        if (!payload.Matches(item)) throw new InvalidDataException("content_integrity");
        return payload;
    }

    public async Task WatchAsync(Action changed, Action<bool> connected, CancellationToken cancellationToken)
    {
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            try
            {
                var uri = new UriBuilder(origin) { Scheme = "wss", Path = "/v1/events" }.Uri;
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(uri, connect.Token).ConfigureAwait(false);
                connected(true); changed(); failures = 0;
                byte[] buffer = new byte[4096];
                while (!cancellationToken.IsCancellationRequested)
                {
                    int count = 0;
                    ValueWebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                        if (frame.MessageType != WebSocketMessageType.Text) throw new WebSocketException();
                        count += frame.Count;
                        if (count == buffer.Length && !frame.EndOfMessage) throw new InvalidDataException("event_limit");
                    } while (!frame.EndOfMessage);
                    var hint = JsonSerializer.Deserialize<ChangeEvent>(buffer.AsSpan(0, count), ClientJson);
                    if (hint?.Type != "changed") throw new InvalidDataException("invalid_event");
                    changed(); // Event ordering never overrides REST state.
                }
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
            {
                connected(false);
                if (cancellationToken.IsCancellationRequested) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(++failures, 4)))) +
                    TimeSpan.FromMilliseconds(Random.Shared.Next(500)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static HttpRequestMessage Mutation(HttpMethod method, string path, string etag, Guid key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("If-Match", etag);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return request;
    }

    private async Task<T> JsonAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40)); cancellationToken = deadline.Token;
        using (request)
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            RequireSuccess(response);
            return JsonSerializer.Deserialize<T>(await ReadBoundedAsync(response.Content, 32768, cancellationToken).ConfigureAwait(false), ClientJson)
                ?? throw new InvalidDataException("invalid_response");
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("response_limit");
        using var output = new MemoryStream();
        using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[16384];
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)output.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw new InvalidDataException("response_limit");
            output.Write(buffer, 0, count);
        }
    }

    public static void ValidateState(State state)
    {
        if (state.ProtocolVersion != 1 || !Guid.TryParse(state.ServerEpoch, out _) ||
            !Revision().IsMatch(state.Revision) || !Etag().IsMatch(state.Etag)) throw new InvalidDataException("invalid_state");
        if (state.Item is { } item && (!Identifier().IsMatch(item.ItemId) || !Identifier().IsMatch(item.SourceDeviceId) ||
            item.CryptoMode != "none" || !Hash().IsMatch(item.Sha256) || item.ByteLength < 0 ||
            item.Kind is not ("text" or "image") || item.ExpiresAt <= item.CommittedAt)) throw new InvalidDataException("invalid_item");
    }

    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new CloudRequestException((int)response.StatusCode);
        if (response.Headers.CacheControl?.NoStore != true) throw new InvalidDataException("no_store_required");
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    [GeneratedRegex("^(0|[1-9][0-9]{0,100})$", RegexOptions.CultureInvariant)] private static partial Regex Revision();
    [GeneratedRegex("^\"[A-Za-z0-9_-]{1,128}\"$", RegexOptions.CultureInvariant)] private static partial Regex Etag();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Hash();
    public void Dispose() => client.Dispose();
}
