using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ContinuityBridge.Contracts;
using ContinuityBridge.Relay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Relay.Tests;

[TestClass]
public sealed class ProcessTests
{
    [TestMethod]
    public async Task RealTlsWebSocketReconnectRevocationAndProcessEpoch()
    {
        await using var host = await Host.StartAsync();
        using var socket = await host.ConnectAsync();
        var first = await Hint(socket);
        var initial = await host.StateAsync();
        Assert.AreEqual(initial.ServerEpoch, first.ServerEpoch);
        var requestKey = Guid.NewGuid();
        using var response = await host.PutAsync(initial.Etag, requestKey, "TLS 合成 🧪\r\n");
        var receipt = (await response.Content.ReadFromJsonAsync<MutationReceipt>(Wire.Json))!;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(receipt.State.Revision, (await Hint(socket)).Revision);
        socket.Abort();
        using var reconnected = await host.ConnectAsync();
        Assert.AreEqual(receipt.State.Revision, (await Hint(reconnected)).Revision);
        await host.RevokeAsync();
        using var unauthorized = await host.Client.GetAsync("/v1/clipboard");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await reconnected.ReceiveAsync(new byte[4096], timeout.Token);
        Assert.AreEqual(WebSocketMessageType.Close, frame.MessageType);
        await reconnected.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        await host.RestoreAsync();
        await host.RestartAsync();
        var restarted = await host.StateAsync();
        Assert.AreNotEqual(initial.ServerEpoch, restarted.ServerEpoch);
        Assert.IsNull(restarted.Item); Assert.AreEqual("0", restarted.Revision);
        using var old = await host.PutAsync(initial.Etag, requestKey, "TLS 合成 🧪\r\n");
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, old.StatusCode);
        using var afterRestart = await host.ConnectAsync();
        Assert.AreEqual(restarted.ServerEpoch, (await Hint(afterRestart)).ServerEpoch);
    }

    [TestMethod]
    public async Task RevokedTokenCannotReplayCompletedMutation()
    {
        await using var host = await Host.StartAsync();
        var initial = await host.StateAsync(); var key = Guid.NewGuid();
        using var accepted = await host.PutAsync(initial.Etag, key, "revocation fixture");
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        await host.RevokeAsync();
        using var replay = await host.PutAsync(initial.Etag, key, "revocation fixture");
        Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [TestMethod]
    public async Task ConfiguredLimitsMatchCapabilitiesAndEnforcement()
    {
        await using var host = await Host.StartAsync(retention: 9, maxText: 128);
        var caps = (await host.Client.GetFromJsonAsync<Capabilities>("/v1/capabilities", Wire.Json))!;
        Assert.AreEqual(9, caps.RetentionSeconds); Assert.AreEqual(128, caps.Limits.MaxTextUtf8Bytes);
        var state = await host.StateAsync();
        using var accepted = await host.PutAsync(state.Etag, Guid.NewGuid(), new string('x', 128));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        var result = (await accepted.Content.ReadFromJsonAsync<MutationReceipt>(Wire.Json))!;
        Assert.AreEqual(TimeSpan.FromSeconds(9), result.Result.Item!.ExpiresAt - result.Result.Item.CommittedAt);
        using var rejected = await host.PutAsync(result.State.Etag, Guid.NewGuid(), new string('x', 129));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        Assert.AreEqual(result.State, await host.StateAsync());
    }

    [TestMethod]
    public async Task IncompleteDisconnectedUploadCannotCommit()
    {
        await using var host = await Host.StartAsync();
        var state = await host.StateAsync();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
            using var tls = new SslStream(tcp.GetStream(), false, host.ValidateCertificate);
            await tls.AuthenticateAsClientAsync("localhost");
            string headers = $"POST /v1/items/text HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {host.Token}\r\nContent-Type: application/json\r\nIf-Match: {state.Etag}\r\nIdempotency-Key: {Guid.NewGuid()}\r\nContent-Length: 10000\r\n\r\n{{\"text\":\"partial";
            await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes(headers));
        }
        await Task.Delay(200);
        Assert.AreEqual(state, await host.StateAsync());
        using var accepted = await host.PutAsync(state.Etag, Guid.NewGuid(), "complete fixture");
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
    }

    [TestMethod]
    public async Task UnreadLostHttpResponseReplaysTheOriginalCommit()
    {
        await using var host = await Host.StartAsync();
        var state = await host.StateAsync(); var key = Guid.NewGuid();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
            using var tls = new SslStream(tcp.GetStream(), false, host.ValidateCertificate);
            await tls.AuthenticateAsClientAsync("localhost");
            byte[] json = "{\"text\":\"lost response fixture\"}"u8.ToArray();
            string headers = $"POST /v1/items/text HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {host.Token}\r\nContent-Type: application/json\r\nIf-Match: {state.Etag}\r\nIdempotency-Key: {key}\r\nContent-Length: {json.Length}\r\n\r\n";
            await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes(headers));
            await tls.WriteAsync(json);
            // Observe commit on a separate connection, never consume the upload response.
            for (int i = 0; i < 50 && (await host.StateAsync()).Etag == state.Etag; i++) await Task.Delay(20);
            Assert.AreNotEqual(state.Etag, (await host.StateAsync()).Etag);
        }
        var committed = await host.StateAsync();
        using var retry = await host.PutAsync(state.Etag, key, "lost response fixture");
        Assert.AreEqual(HttpStatusCode.OK, retry.StatusCode);
        var receipt = (await retry.Content.ReadFromJsonAsync<MutationReceipt>(Wire.Json))!;
        Assert.IsTrue(receipt.Replayed); Assert.AreEqual(committed, receipt.Result); Assert.AreEqual(committed, receipt.State);
    }

    [TestMethod]
    public void ProvisioningUsesFreshRandomTokensAndHashOnlyRegistry()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "global.json"))) root = Directory.GetParent(root)!.FullName;
        string directory = Path.Combine(root, "artifacts", "relay-provision-test", Guid.NewGuid().ToString("N"));
        DeviceRegistry.Provision(directory);
        using var clients = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "client-tokens.json")));
        string token = clients.RootElement[0].GetProperty("token").GetString()!;
        Assert.AreEqual(64, token.Length);
        Assert.AreNotEqual(token, clients.RootElement[1].GetProperty("token").GetString());
        var registry = new DeviceRegistry(Path.Combine(directory, "devices.json"));
        Assert.AreEqual(clients.RootElement[0].GetProperty("deviceId").GetString(), registry.Authenticate("Bearer " + token).DeviceId);
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "devices.json")).Contains(token, StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeviceRegistry.Provision(directory));
    }

    [TestMethod]
    public async Task HttpExpiryPreservesOriginalReceiptWithoutRevival()
    {
        await using var host = await Host.StartAsync(retention: 1);
        var initial = await host.StateAsync(); var key = Guid.NewGuid();
        using var response = await host.PutAsync(initial.Etag, key, "expiry fixture");
        var put = (await response.Content.ReadFromJsonAsync<MutationReceipt>(Wire.Json))!;
        await Task.Delay(1150);
        using var old = await host.Client.GetAsync($"/v1/items/{put.Result.Item!.ItemId}/content");
        Assert.AreEqual(HttpStatusCode.Gone, old.StatusCode);
        Assert.IsNull((await host.StateAsync()).Item);
        using var retry = await host.PutAsync(initial.Etag, key, "expiry fixture");
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, retry.StatusCode);
    }

    [TestMethod]
    public async Task UploadAdmissionCancellationAndTimeoutPreserveOldItem()
    {
        await using var host = await Host.StartAsync(uploadTimeout: 1, uploads: 1);
        var initial = await host.StateAsync();
        using var put = await host.PutAsync(initial.Etag, Guid.NewGuid(), "old fixture");
        var state = await host.StateAsync();
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        using var tls = new SslStream(tcp.GetStream(), false, host.ValidateCertificate);
        await tls.AuthenticateAsClientAsync("localhost");
        string headers = $"POST /v1/items/text HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {host.Token}\r\nContent-Type: application/json\r\nIf-Match: {state.Etag}\r\nIdempotency-Key: {Guid.NewGuid()}\r\nTransfer-Encoding: chunked\r\n\r\n1\r\n{{\r\n";
        await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes(headers));
        await Task.Delay(150);
        using var busy = await host.PutAsync(state.Etag, Guid.NewGuid(), "blocked fixture");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, busy.StatusCode);
        await Task.Delay(1100);
        Assert.AreEqual(state, await host.StateAsync());
        using var recovered = await host.PutAsync(state.Etag, Guid.NewGuid(), "recovered fixture");
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
    }

    [TestMethod]
    public async Task SlowDownloadReplacementAndExpiryEndWithinDeadline()
    {
        foreach (bool expire in new[] { false, true })
        {
            await using var host = await Host.StartAsync(retention: expire ? 2 : 30, downloadTimeout: 2, maxText: 4_000_000);
            var state = await host.StateAsync();
            using var put = await host.PutAsync(state.Etag, Guid.NewGuid(), new string('x', 4_000_000));
            Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
            var before = await host.StateAsync();
            using var response = await host.Client.GetAsync($"/v1/items/{before.Item!.ItemId}/content", HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync();
            byte[] one = new byte[1]; Assert.AreEqual(1, await stream.ReadAsync(one));
            if (!expire)
            {
                using var replace = await host.PutAsync(before.Etag, Guid.NewGuid(), "replacement fixture");
                Assert.AreEqual(HttpStatusCode.OK, replace.StatusCode);
            }
            await Task.Delay(2200);
            using var old = await host.Client.GetAsync($"/v1/items/{before.Item.ItemId}/content");
            Assert.AreEqual(HttpStatusCode.Gone, old.StatusCode);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            long read = 1; byte[] buffer = new byte[16384];
            try { int n; while ((n = await stream.ReadAsync(buffer, deadline.Token)) != 0) read += n; }
            catch (HttpIOException) { }
            catch (IOException) { }
            Assert.IsFalse(deadline.IsCancellationRequested, "Retired download must terminate within bounded time.");
            Assert.IsLessThanOrEqualTo((long)before.Item.ByteLength, read);
            // A complete response is permitted when the transport already buffered it.
            // The authoritative final metadata check must still reject applying this item.
            Assert.AreNotEqual(before.Item.ItemId, (await host.StateAsync()).Item?.ItemId);
        }
    }

    private static async Task<ChangeEvent> Hint(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] bytes = new byte[4096];
        var result = await socket.ReceiveAsync(bytes, timeout.Token);
        Assert.AreEqual(WebSocketMessageType.Text, result.MessageType); Assert.IsTrue(result.EndOfMessage);
        return JsonSerializer.Deserialize<ChangeEvent>(bytes.AsSpan(0, result.Count), Wire.Json)!;
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string root;
        private readonly string directory;
        private readonly string registry;
        private readonly string pin;
        private readonly string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        private readonly int retention, uploadTimeout, downloadTimeout, uploads, maxText;
        private Process? process;
        private readonly HttpClientHandler handler;
        public int Port { get; }
        public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        public HttpClient Client { get; }
        private Host(int retention, int uploadTimeout, int downloadTimeout, int uploads, int maxText)
        {
            this.retention = retention; this.uploadTimeout = uploadTimeout; this.downloadTimeout = downloadTimeout; this.uploads = uploads; this.maxText = maxText;
            root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "global.json"))) root = Directory.GetParent(root)!.FullName;
            directory = Path.Combine(root, "artifacts", "relay-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            registry = Path.Combine(directory, "devices.json");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            pin = cert.GetCertHashString();
            File.WriteAllBytes(Path.Combine(directory, "test.pfx"), cert.Export(X509ContentType.Pfx, password));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == pin };
            Client = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{Port}"), Timeout = TimeSpan.FromSeconds(15) };
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }
        public static async Task<Host> StartAsync(int retention = 30, int uploadTimeout = 5, int downloadTimeout = 5, int uploads = 2, int maxText = 1_000_000)
        {
            var host = new Host(retention, uploadTimeout, downloadTimeout, uploads, maxText);
            try { await host.RestoreAsync(); await host.RestartAsync(); return host; }
            catch { await host.DisposeAsync(); throw; }
        }
        public bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) => certificate?.GetCertHashString() == pin;
        public Task RestoreAsync() => WriteRegistry(false);
        public Task RevokeAsync() => WriteRegistry(true);
        private async Task WriteRegistry(bool revoked)
        {
            var entry = new DeviceRegistration("synthetic-device", Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(Token))), revoked);
            string replacement = registry + ".new";
            await File.WriteAllBytesAsync(replacement, JsonSerializer.SerializeToUtf8Bytes(new[] { entry }, Wire.Json));
            File.Move(replacement, registry, true);
        }
        public async Task RestartAsync()
        {
            if (process is not null) { process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); }
            string localSdk = Path.Combine(root, ".tools", "dotnet", "dotnet.exe");
            string sdk = Environment.GetEnvironmentVariable("CONTINUITYBRIDGE_DOTNET") ??
                (OperatingSystem.IsWindows() && File.Exists(localSdk) ? localSdk : "dotnet");
            string dll = Path.Combine(root, "src", "ContinuityBridge.Relay", "bin", "Release", "net10.0", "ContinuityBridge.Relay.dll");
            var start = new ProcessStartInfo(sdk) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(dll);
            start.Environment["ASPNETCORE_URLS"] = $"https://127.0.0.1:{Port}";
            start.Environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = Path.Combine(directory, "test.pfx");
            start.Environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = password;
            start.Environment["Relay__DeviceFile"] = registry;
            start.Environment["Relay__RetentionSeconds"] = retention.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Relay__IdempotencyRetentionSeconds"] = retention.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Relay__UploadTimeoutSeconds"] = uploadTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Relay__DownloadTimeoutSeconds"] = downloadTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Relay__MaxUploads"] = uploads.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Relay__MaxTextUtf8Bytes"] = maxText.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Relay__MaxTextJsonBytes"] = (8 * maxText).ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["Relay__MemoryBudgetBytes"] = "1000000000";
            process = Process.Start(start)!;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            for (int i = 0; i < 100; i++)
            {
                if (process.HasExited) Assert.Fail("Relay process exited before readiness.");
                try { using var health = await Client.GetAsync("/healthz"); if (health.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            Assert.Fail("Relay readiness timeout.");
        }
        public async Task<State> StateAsync() => (await Client.GetFromJsonAsync<State>("/v1/clipboard", Wire.Json))!;
        public async Task<HttpResponseMessage> PutAsync(string etag, Guid key, string text)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/items/text") { Content = JsonContent.Create(new { text }) };
            message.Headers.TryAddWithoutValidation("If-Match", etag); message.Headers.Add("Idempotency-Key", key.ToString());
            return await Client.SendAsync(message);
        }
        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + Token);
            socket.Options.RemoteCertificateValidationCallback = ValidateCertificate;
            await socket.ConnectAsync(new Uri($"wss://localhost:{Port}/v1/events"), CancellationToken.None);
            return socket;
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); handler.Dispose();
            if (process is not null)
            { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
        }
    }
}
