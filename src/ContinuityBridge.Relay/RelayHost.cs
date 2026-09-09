using System.Net.WebSockets;
using System.Text.Json;
using ContinuityBridge.Contracts;

namespace ContinuityBridge.Relay;

public static class RelayHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Do not inherit verbose hosting logs containing URLs/item IDs, body hashes or headers.
        builder.Logging.ClearProviders();
        var options = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new();
        options.Validate();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxConcurrentConnections = 128;
            server.Limits.MaxConcurrentUpgradedConnections = options.MaxWebSockets;
            // Kestrel's chunked limit also charges framing bytes. The admitted reader
            // enforces exact decoded wire bytes (including limit+1) for every encoding.
            server.Limits.MaxRequestBodySize = null;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            server.Limits.Http2.MaxStreamsPerConnection = 16;
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ClipboardStore>();
        builder.Services.AddSingleton(new DeviceRegistry(options.DeviceFile));
        builder.Services.AddHostedService<Maintenance>();
        var app = builder.Build();
        var registry = app.Services.GetRequiredService<DeviceRegistry>();
        var store = app.Services.GetRequiredService<ClipboardStore>();
        var uploads = new SemaphoreSlim(options.MaxUploads);
        var downloads = new SemaphoreSlim(options.MaxDownloads);
        var sockets = new SemaphoreSlim(options.MaxWebSockets);
        app.Lifetime.ApplicationStopped.Register(() => { uploads.Dispose(); downloads.Dispose(); sockets.Dispose(); });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try
            {
                if (context.Request.Path.StartsWithSegments("/v1"))
                {
                    var identity = registry.Authenticate(context.Request.Headers.Authorization.ToString());
                    context.Items["device"] = identity;
                    if (context.Request.Headers.ContainsKey("Content-Encoding")) throw new ProtocolException(415, "unsupported_media_type");
                    if (context.Request.Headers.ContainsKey("X-CB-Key-Id") || context.Request.Headers.ContainsKey("X-CB-Nonce"))
                        throw new ProtocolException(400, "invalid_request");
                    string mode = context.Request.Headers["X-CB-Crypto-Mode"].ToString();
                    if (mode.Length > 0 && mode != "none") throw new ProtocolException(415, "unsupported_crypto_mode");
                }
                await next(context).ConfigureAwait(false);
            }
            catch (ProtocolException ex) { await Error(context, ex.Status, ex.Code).ConfigureAwait(false); }
            catch (BadHttpRequestException ex) { await Error(context, ex.StatusCode, ex.StatusCode == 413 ? "payload_too_large" : "invalid_request").ConfigureAwait(false); }
            catch (OperationCanceledException) { await Error(context, 408, "request_timeout").ConfigureAwait(false); }
            catch (Exception) { await Error(context, 500, "internal_error").ConfigureAwait(false); }
        });
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
        app.MapGet("/v1/capabilities", () => Results.Json(options.Capabilities(), Wire.Json));
        app.MapGet("/v1/clipboard", (HttpContext context) =>
        {
            var state = store.GetState();
            context.Response.Headers.ETag = state.Etag;
            return Results.Json(state, Wire.Json);
        });
        app.MapPost("/v1/items/text", (HttpContext context) => Mutate(context, "text"));
        app.MapPost("/v1/items/image", (HttpContext context) => Mutate(context, "image"));
        app.MapDelete("/v1/clipboard", (HttpContext context) => Mutate(context, "clear"));
        app.MapGet("/v1/items/{itemId}/content", async (HttpContext context, string itemId) =>
        {
            if (!RequestValidation.IsIdentifier(itemId)) throw new ProtocolException(400, "invalid_request");
            if (!await downloads.WaitAsync(0, context.RequestAborted).ConfigureAwait(false)) throw new ProtocolException(429, "rate_limited");
            try
            {
                using var lease = store.Open(itemId);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.Retired);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.DownloadTimeoutSeconds));
                context.Response.ContentType = lease.Metadata.MimeType;
                context.Response.ContentLength = lease.Metadata.ByteLength;
                for (int offset = 0; offset < lease.Body.Length; offset += 16384)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    // Revalidate under the same state lock used by expiry and CAS.
                    if (store.GetState().Item?.ItemId != itemId || !registry.IsActive(Identity(context))) throw new OperationCanceledException();
                    await context.Response.Body.WriteAsync(lease.Body.Slice(offset, Math.Min(16384, lease.Body.Length - offset)), timeout.Token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { context.Abort(); }
            finally { downloads.Release(); }
        });
        app.MapGet("/v1/events", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) throw new ProtocolException(400, "invalid_request");
            if (!await sockets.WaitAsync(0, context.RequestAborted).ConfigureAwait(false)) throw new ProtocolException(429, "rate_limited");
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var receiver = ReceiveClose(socket, lifetime);
                try
                {
                    string? observed = null;
                    int ticks = 0;
                    while (!lifetime.IsCancellationRequested)
                    {
                        if (!registry.IsActive(Identity(context))) break;
                        var state = store.GetState();
                        if (state.Etag != observed || ++ticks >= 60)
                        {
                            byte[] hint = JsonSerializer.SerializeToUtf8Bytes(new ChangeEvent("changed", state.ServerEpoch, state.Revision), Wire.Json);
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            deadline.CancelAfter(TimeSpan.FromSeconds(5));
                            await socket.SendAsync(hint, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
                            observed = state.Etag; ticks = 0;
                        }
                        await Task.Delay(250, lifetime.Token).ConfigureAwait(false);
                    }
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Reconnect and authenticate.", close.Token).ConfigureAwait(false);
                        // Give the peer a bounded chance to receive/acknowledge close.
                        // Cancelling its outstanding receive immediately aborts the
                        // transport and can discard the just-written TLS close frame.
                        try { await receiver.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                        catch (TimeoutException) { }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                finally { await lifetime.CancelAsync().ConfigureAwait(false); socket.Abort(); await receiver.ConfigureAwait(false); }
            }
            finally { sockets.Release(); }
        });
        return app;

        async Task Mutate(HttpContext context, string kind)
        {
            var (etag, key) = RequestValidation.Conditions(context.Request);
            if (!await uploads.WaitAsync(0, context.RequestAborted).ConfigureAwait(false)) throw new ProtocolException(429, "rate_limited");
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(options.UploadTimeoutSeconds));
                byte[]? body = null;
                string contentType = context.Request.ContentType ?? "";
                string mime = "";
                if (kind == "text")
                {
                    var parts = contentType.Split(';', StringSplitOptions.TrimEntries);
                    if (!parts[0].Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                        parts.Skip(1).Any(p => !p.Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase)))
                        throw new ProtocolException(415, "unsupported_media_type");
                    body = RequestValidation.Text(await RequestValidation.ReadBoundedAsync(context.Request, options.MaxTextJsonBytes, deadline.Token).ConfigureAwait(false), options);
                    mime = "text/plain; charset=utf-8";
                }
                else if (kind == "image")
                {
                    mime = contentType.ToLowerInvariant();
                    if (mime is not ("image/png" or "image/jpeg")) throw new ProtocolException(415, "unsupported_media_type");
                    body = await RequestValidation.ReadBoundedAsync(context.Request, options.MaxImageBytes, deadline.Token).ConfigureAwait(false);
                    ImageValidation.Validate(body, mime, options.MaxDecodedPixels, deadline.Token);
                }
                else
                {
                    mime = contentType.ToLowerInvariant();
                    if ((await RequestValidation.ReadBoundedAsync(context.Request, 0, deadline.Token).ConfigureAwait(false)).Length != 0)
                        throw new ProtocolException(400, "invalid_request");
                }
                registry.Reload();
                if (!registry.IsActive(Identity(context))) throw new ProtocolException(401, "unauthorized");
                string fingerprint = RequestValidation.Fingerprint(context.Request.Method, context.Request.Path.Value!,
                    kind == "text" ? "application/json" : mime, "none", etag, body);
                var receipt = store.Commit(Identity(context).DeviceId, key, fingerprint, etag, body, mime, deadline.Token);
                context.Response.Headers.ETag = receipt.State.Etag;
                await context.Response.WriteAsJsonAsync(receipt, Wire.Json, deadline.Token).ConfigureAwait(false);
            }
            finally { uploads.Release(); }
        }
    }

    private static DeviceIdentity Identity(HttpContext context) => (DeviceIdentity)context.Items["device"]!;
    private static async Task ReceiveClose(WebSocket socket, CancellationTokenSource lifetime)
    {
        try
        {
            byte[] buffer = new byte[1];
            // Client payloads are not part of this hint-only protocol. One byte/close
            // wakes the sender to close; there is no incoming message accumulator.
            await socket.ReceiveAsync(buffer, lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        finally { await lifetime.CancelAsync().ConfigureAwait(false); }
    }
    private static async Task Error(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
        context.Response.Clear();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = status;
        if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = new { code, message = "Request rejected; refresh state or check request configuration.", requestId = Guid.NewGuid().ToString() } }, Wire.Json).ConfigureAwait(false);
    }

    private sealed class Maintenance(ClipboardStore store, DeviceRegistry registry) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do { registry.Reload(); store.GetState(); }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
    }
}
