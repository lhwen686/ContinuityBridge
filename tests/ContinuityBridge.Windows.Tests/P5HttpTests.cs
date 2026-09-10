using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Qa.Sidecar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
[TestCategory("P5")]
public sealed class P5HttpTests
{
    [TestMethod]
    public async Task RealHttpsIdentityTextImageCasReplayAndRevokedIdentity()
    {
        await using var host = await P5TlsHost.Start();
        using var windows = new CloudTransport(host.Origin, host.TokenA, host.Handler());
        using var phone = new CloudTransport(host.Origin, host.TokenB, host.Handler());
        Assert.AreEqual("windows", await windows.IdentityAsync(CancellationToken.None));
        Assert.AreEqual("phone", await phone.IdentityAsync(CancellationToken.None));
        var limits = (await windows.CapabilitiesAsync(CancellationToken.None)).Limits;
        var initial = await windows.StateAsync(CancellationToken.None);
        var longText = FixtureCatalog.Get("long-text-v1");
        var payload = new ClipboardPayload(longText.Kind, longText.MimeType, longText.Bytes);
        var key = Guid.NewGuid();
        var first = await windows.PutAsync(payload, initial.Etag, key, CancellationToken.None);
        var downloaded = await phone.DownloadAsync(first.State.Item!, limits, CancellationToken.None);
        CollectionAssert.AreEqual(payload.Bytes, downloaded.Bytes);
        var replay = await windows.PutAsync(payload, initial.Etag, key, CancellationToken.None);
        Assert.IsTrue(replay.Replayed); Assert.AreEqual(first.Result.Etag, replay.Result.Etag);
        var stale = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => phone.PutAsync(payload, initial.Etag, Guid.NewGuid(), CancellationToken.None));
        Assert.AreEqual(412, stale.StatusCode);
        var png = FixtureCatalog.Get("alpha-png-v1");
        var image = await phone.PutAsync(new(png.Kind, png.MimeType, png.Bytes), first.State.Etag, Guid.NewGuid(), CancellationToken.None);
        CollectionAssert.AreEqual(png.Bytes, (await windows.DownloadAsync(image.State.Item!, limits, CancellationToken.None)).Bytes);
        using var invalid = new CloudTransport(host.Origin, new string('0', 64), host.Handler());
        var denied = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => invalid.IdentityAsync(CancellationToken.None));
        Assert.AreEqual(401, denied.StatusCode);
    }

    [TestMethod]
    public async Task QaHttpsRejectsRolesUnknownPropertiesWrongShaAndDuplicateSession()
    {
        await using var host = await P5TlsHost.Start(qa: true);
        using var client = new HttpClient(host.Handler()) { BaseAddress = new Uri(host.Origin) };
        var lease = host.Lease!; string controller = Guid.NewGuid().ToString(), runner = Guid.NewGuid().ToString();
        var command = new QaCommand(lease.RunId, lease.CandidateSha, controller, Guid.NewGuid().ToString(), "SetFixtureClipboard", "unicode-v1");
        using var noAuth = await client.GetAsync("/qa/v1/status"); Assert.AreEqual(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        using var wrongRole = await Post("/qa/v1/commands", command, host.TokenB); Assert.AreEqual(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        using var wrongSha = await Post("/qa/v1/commands", command with { CandidateSha = new string('0', 40) }, host.TokenA);
        Assert.AreEqual(HttpStatusCode.Conflict, wrongSha.StatusCode);
        using var path = await Post("/qa/v1/commands", command with { FixtureId = "C:/private.txt" }, host.TokenA); Assert.AreEqual(HttpStatusCode.BadRequest, path.StatusCode);
        using var arbitrary = new HttpRequestMessage(HttpMethod.Post, "/qa/v1/commands")
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, QaWire.Json)[..^1] + ",\"body\":\"forbidden\"}")) };
        arbitrary.Headers.Authorization = new("Bearer", host.TokenA); arbitrary.Content.Headers.ContentType = new("application/json");
        using var rejected = await client.SendAsync(arbitrary); Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var duplicateProperty = new HttpRequestMessage(HttpMethod.Post, "/qa/v1/commands")
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, QaWire.Json)[..^1] + ",\"action\":\"Status\"}")) };
        duplicateProperty.Headers.Authorization = new("Bearer", host.TokenA); duplicateProperty.Content.Headers.ContentType = new("application/json");
        using var rejectedDuplicate = await client.SendAsync(duplicateProperty);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejectedDuplicate.StatusCode);
        using var accepted = await Post("/qa/v1/commands", command, host.TokenA); Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        using var poll = await Post("/qa/v1/poll", new QaPoll(lease.RunId, lease.CandidateSha, runner), host.TokenB); Assert.AreEqual(HttpStatusCode.OK, poll.StatusCode);
        using var secondRunner = await Post("/qa/v1/poll", new QaPoll(lease.RunId, lease.CandidateSha, Guid.NewGuid().ToString()), host.TokenB);
        Assert.AreEqual(HttpStatusCode.Conflict, secondRunner.StatusCode);
        using var secondController = await Post("/qa/v1/commands", command with { SessionId = Guid.NewGuid().ToString() }, host.TokenA);
        Assert.AreEqual(HttpStatusCode.Conflict, secondController.StatusCode);
        var result = new QaResult(lease.RunId, lease.CandidateSha, runner, command.CommandId, "PASS");
        using var done = await Post("/qa/v1/result", result, host.TokenB); Assert.AreEqual(HttpStatusCode.OK, done.StatusCode);
        using var duplicate = await Post("/qa/v1/result", result, host.TokenB); Assert.AreEqual(HttpStatusCode.OK, duplicate.StatusCode);
        using var freeText = await Post("/qa/v1/result", result with { Result = "private clipboard text" }, host.TokenB);
        Assert.AreEqual(HttpStatusCode.BadRequest, freeText.StatusCode);

        async Task<HttpResponseMessage> Post<T>(string route, T value, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, QaWire.Json)) };
            request.Headers.Authorization = new("Bearer", token); request.Content.Headers.ContentType = new("application/json");
            return await client.SendAsync(request);
        }
    }

    [TestMethod]
    public void QaLeaseExpiryAndActionTimeoutAreEnforcedWithMonotonicTime()
    {
        var time = new TestTime();
        var lease = new QaLease(Guid.NewGuid().ToString(), new string('a', 40), time.GetUtcNow().AddMinutes(3), new string('b', 64), new string('c', 64));
        var mailbox = new QaMailbox(lease, lease.CandidateSha, time);
        mailbox.Submit(new(lease.RunId, lease.CandidateSha, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "Status", null));
        time.Advance(121);
        Assert.IsTrue(mailbox.Status().Ended); Assert.AreEqual("BLOCKED", mailbox.Status().Result);
        time.Advance(60);
        Assert.ThrowsExactly<QaRequestException>(() => mailbox.Status());
    }

    private sealed class TestTime : TimeProvider
    {
        private long seconds;
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => seconds;
        public override DateTimeOffset GetUtcNow() => start.AddSeconds(seconds);
        public void Advance(int value) => seconds += value;
    }
}
