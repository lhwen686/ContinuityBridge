using System.Text;
using System.Text.Json;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Core;
using ContinuityBridge.TestAgent;
using ContinuityBridge.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
[TestCategory("P5")]
public sealed class TestAgentSafetyTests
{
    [TestMethod]
    public async Task DisabledOrCancelledHasNoQaOrClipboardAccess()
    {
        using var disabled = new Scenario();
        await disabled.Session.RunAsync(false, CancellationToken.None);
        Assert.AreEqual(0, disabled.Qa.Polls); Assert.AreEqual(0, disabled.Memory.Reads); Assert.AreEqual(0, disabled.Memory.Writes);
        using var cancelled = new Scenario();
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await cancelled.Session.RunAsync(true, ct.Token);
        Assert.AreEqual(0, cancelled.Memory.Reads); Assert.AreEqual(0, cancelled.Memory.Writes);
    }

    [TestMethod]
    public async Task InvalidIdentityRoleRunShaActivityAndLeaseNeverReadOrWrite()
    {
        Func<QaSnapshot, QaSnapshot>[] bad =
        [
            s => s with { Service = "other" }, s => s with { Role = "controller" }, s => s with { Role = "" },
            s => s with { SessionId = Guid.NewGuid().ToString() }, s => s with { RunId = Guid.NewGuid().ToString() },
            s => s with { CandidateSha = new string('b', 40) }, s => s with { RunnerReady = false }, s => s with { Ended = true },
            s => s with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, s => s with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
        ];
        foreach (var mutate in bad)
        {
            using var scenario = new Scenario(); scenario.Qa.Mutate = mutate;
            await scenario.Session.RunAsync(true, CancellationToken.None);
            Assert.AreEqual(0, scenario.Memory.Reads); Assert.AreEqual(0, scenario.Memory.Writes);
        }
        using var unreachable = new Scenario(); unreachable.Qa.Fail = true;
        await unreachable.Session.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(0, unreachable.Memory.Reads); Assert.AreEqual(0, unreachable.Memory.Writes);
    }

    [TestMethod]
    public async Task SupportedTextAndImagesAreIndependentAndRestoreAfterSyncStops()
    {
        foreach (string kind in new[] { "text", "PNG", "JFIF", "DIB", "DIBV5", "bitmap", "empty" })
        {
            using var s = new Scenario();
            s.Memory.Data.Clear();
            uint format = kind switch { "text" => 13, "DIB" or "bitmap" => 8, "DIBV5" => 17, _ => s.Memory.Format(kind) };
            byte[] body = kind == "text" ? Encoding.Unicode.GetBytes("synthetic local backup 🧪\0") : FixtureCatalog.Get(kind == "JFIF" ? "jpeg-v1" : "alpha-png-v1").Bytes;
            if (kind is "DIB" or "DIBV5" or "bitmap")
                body = ImageClipboardCodec.Dib(ImageClipboardCodec.Decode(body, "image/png", 16_000_000), kind == "DIBV5");
            if (kind != "empty") s.Memory.Data[format] = body.ToArray();
            if (kind == "bitmap") s.Memory.Advertised = [2];
            s.Desktop.AfterCapture = () => { foreach (var bytes in s.Memory.Data.Values) Array.Fill<byte>(bytes, 0); };
            var result = await s.Session.RunAsync(true, CancellationToken.None);
            Assert.AreEqual(RestoreOutcome.Restored, result.Restore);
            Assert.IsTrue(s.Sync.Stopped); Assert.IsTrue(s.Desktop.RestoredAfterSyncStop);
            if (kind != "empty") CollectionAssert.AreEqual(body, s.Memory.Data[format]);
            Assert.AreEqual(2, s.Memory.Writes);
            Assert.IsTrue(s.Memory.UploadDenied);
            Assert.IsTrue(s.Memory.HistoryDenied);
            Assert.HasCount(1, s.Qa.Results);
            Assert.AreEqual("STOPPED", s.Qa.Results[0].Result);
            Assert.IsFalse(JsonSerializer.Serialize(s.Qa.Results).Contains("synthetic local backup", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task NewCopyBetweenSnapshotAndFirstWriteIsNeverOverwritten()
    {
        using var s = new Scenario();
        s.Desktop.AfterCapture = () => s.Memory.ExternalCopy("new synthetic copy");
        var result = await s.Session.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(0, s.Memory.Writes); Assert.AreEqual(RestoreOutcome.Untouched, result.Restore);
        Assert.AreEqual("new synthetic copy\0", Encoding.Unicode.GetString(s.Memory.Data[13]));
    }

    [TestMethod]
    public async Task ExternalCopyIncludingIdenticalFixtureIsNeverRestoredOver()
    {
        foreach (bool same in new[] { false, true })
        {
            using var s = new Scenario();
            s.Sync.OnStart = () => s.Memory.ExternalCopy(same ? "sentinel-v1" : "new synthetic copy");
            var result = await s.Session.RunAsync(true, CancellationToken.None);
            Assert.AreEqual(RestoreOutcome.SkippedExternalCopy, result.Restore); Assert.AreEqual(1, s.Memory.Writes);
        }
    }

    [TestMethod]
    public void CasIsCheckedInsideLockForWriteAndRestore()
    {
        var memory = new FakeMemory(); using var guard = new ClipboardTakeover(memory); guard.Capture(CancellationToken.None);
        memory.BeforeNextOpen = () => memory.ExternalCopy("racing first write");
        Assert.IsFalse(guard.Write([new(13, [1, 0, 0, 0])], CancellationToken.None)); Assert.AreEqual(0, memory.Writes);
        var second = new FakeMemory(); using var another = new ClipboardTakeover(second); another.Capture(CancellationToken.None);
        Assert.IsTrue(another.Write([new(13, [1, 0, 0, 0])], CancellationToken.None));
        second.BeforeNextOpen = () => second.ExternalCopy("racing restoration");
        Assert.AreEqual(RestoreOutcome.SkippedExternalCopy, another.Restore(CancellationToken.None)); Assert.AreEqual(1, second.Writes);
    }

    [TestMethod]
    public async Task UnsupportedMixedFormatsBusySnapshotFailureAndLimitsDoNotOverwrite()
    {
        foreach (string failure in new[] { "unknown", "files", "ole", "busy", "read", "oversize", "many", "changed" })
        {
            using var s = new Scenario();
            switch (failure)
            {
                case "unknown": s.Memory.Data[50000] = [1]; break;
                case "files": s.Memory.Data[15] = [1]; break;
                case "ole": s.Memory.Data[0x80] = [1]; break;
                case "busy": s.Memory.FailOpen = true; break;
                case "read": s.Memory.FailRead = true; break;
                case "oversize": s.Memory.Oversize = true; break;
                case "many": s.Memory.Advertised = Enumerable.Range(1, 17).Select(i => (uint)i).ToArray(); break;
                case "changed": s.Memory.AfterCopy = () => s.Memory.Sequence++; break;
            }
            await s.Session.RunAsync(true, CancellationToken.None);
            Assert.AreEqual(0, s.Memory.Writes);
            if (failure is "unknown" or "files" or "ole" or "many") Assert.AreEqual(0, s.Memory.Reads);
        }
    }

    [TestMethod]
    public void TotalSnapshotBudgetAndPartialPublicationFailureAreBounded()
    {
        var memory = new FakeMemory(); memory.Data.Clear();
        memory.Data[8] = new byte[50_000_000]; memory.Data[17] = new byte[50_000_000];
        using (var guard = new ClipboardTakeover(memory))
            Assert.ThrowsExactly<InvalidDataException>(() => guard.Capture(CancellationToken.None));
        Assert.AreEqual(0, memory.Writes);
        var partial = new FakeMemory(); using var takeover = new ClipboardTakeover(partial);
        byte[] original = partial.Data[13].ToArray(); takeover.Capture(CancellationToken.None);
        partial.FailPublish = true;
        Assert.ThrowsExactly<IOException>(() => takeover.Write([new(13, [2, 0, 0, 0])], CancellationToken.None));
        partial.FailPublish = false;
        Assert.AreEqual(RestoreOutcome.Restored, takeover.Restore(CancellationToken.None));
        CollectionAssert.AreEqual(original, partial.Data[13]);
        Assert.AreEqual(RestoreOutcome.Restored, takeover.Restore(CancellationToken.None));
        Assert.AreEqual(2, partial.Writes);
    }

    [TestMethod]
    public async Task CancellationExpiryAndNetworkOrCommandErrorsUseSameCleanup()
    {
        foreach (string failure in new[] { "cancel", "expiry", "network", "command", "ack", "restore" })
        {
            using var s = new Scenario(); using var cancel = new CancellationTokenSource();
            s.Sync.OnStart = () =>
            {
                if (failure == "cancel") cancel.Cancel();
                if (failure == "expiry") s.Time.Advance(TimeSpan.FromHours(1));
                if (failure == "network") s.Qa.Fail = true;
                if (failure == "command") s.Qa.Snapshot = s.Qa.Snapshot with { Command = s.Qa.Snapshot.Command! with { FixtureId = "forbidden" } };
                if (failure == "restore") s.Memory.FailOpen = true;
            };
            if (failure == "command") s.Qa.Mutate = snapshot => snapshot with { Command = snapshot.Command! with { Action = "shell" } };
            if (failure == "ack") s.Qa.BadAck = true;
            var result = await s.Session.RunAsync(true, cancel.Token);
            Assert.IsTrue(s.Sync.Stopped); Assert.IsTrue(s.Desktop.RestoredAfterSyncStop);
            Assert.AreEqual(failure == "restore" ? RestoreOutcome.Failed : RestoreOutcome.Restored, result.Restore);
        }
    }

    [TestMethod]
    public async Task SlowCancelledCommandIsDrainedBeforeRestoreAndCannotWriteLate()
    {
        using var s = new Scenario(); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Qa.Snapshot = s.Qa.Snapshot with { Command = s.Qa.Snapshot.Command! with { Action = "SetFixtureClipboard", FixtureId = "unicode-v1" } };
        s.Desktop.BeforeCommand = async ct => { entered.SetResult(); await release.Task; ct.ThrowIfCancellationRequested(); };
        Task<RunnerOutcome> run = s.Session.RunAsync(true, cancel.Token);
        await entered.Task; cancel.Cancel();
        await Task.Delay(30); Assert.IsFalse(s.Desktop.RestoreCalled);
        release.SetResult();
        var outcome = await run;
        Assert.AreEqual(RestoreOutcome.Restored, outcome.Restore); Assert.AreEqual(2, s.Memory.Writes);
    }

    [TestMethod]
    public async Task DrainTimeoutSkipsRestoreAndLateTaskRemainsCancelled()
    {
        using var s = new Scenario(shortTimeout: true); using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Desktop.BeforeCommand = async ct => { entered.SetResult(); await release.Task; ct.ThrowIfCancellationRequested(); };
        s.Qa.Snapshot = s.Qa.Snapshot with { Command = s.Qa.Snapshot.Command! with { Action = "SetFixtureClipboard", FixtureId = "unicode-v1" } };
        var run = s.Session.RunAsync(true, cancel.Token); await entered.Task; cancel.Cancel();
        var result = await run;
        Assert.IsTrue(result.CleanupTimedOut); Assert.IsFalse(s.Desktop.RestoreCalled); Assert.AreEqual(1, s.Memory.Writes);
        release.SetResult(); await s.Session.Quiesced; Assert.AreEqual(1, s.Memory.Writes);
    }

    [TestMethod]
    public async Task RestoreTimeoutCannotPerformDelayedRestoreAndFailedStopCannotRestore()
    {
        using var s = new Scenario(shortTimeout: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Desktop.BeforeRestore = async ct => { await release.Task; ct.ThrowIfCancellationRequested(); };
        var result = await s.Session.RunAsync(true, CancellationToken.None);
        Assert.IsTrue(result.CleanupTimedOut); Assert.AreEqual(1, s.Memory.Writes);
        release.SetResult(); await s.Session.Quiesced; Assert.AreEqual(1, s.Memory.Writes);
        using var failedStop = new Scenario(); failedStop.Sync.FailStop = true;
        var failed = await failedStop.Session.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(RestoreOutcome.Failed, failed.Restore); Assert.IsFalse(failedStop.Desktop.RestoreCalled);
    }

    [TestMethod]
    public void MissingActivityInSnapshotIsNotImplicitlyTrusted()
    {
        using var s = new Scenario();
        string json = JsonSerializer.Serialize(s.Qa.Snapshot, QaWire.Json).Replace("\"ended\":false,", "", StringComparison.Ordinal);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<QaSnapshot>(json, QaWire.Json));
    }

    [TestMethod]
    public async Task InvalidHttpsResponseCannotReachSnapshot()
    {
        using var scenario = new Scenario();
        using var handler = new RejectingHandler();
        using var qa = new RunnerQa(new Uri("https://synthetic.invalid"), new string('d', 64), handler);
        var session = new RunnerSession(qa, scenario.Desktop, scenario.Sync, scenario.Qa.Snapshot.RunId, scenario.Qa.Snapshot.CandidateSha, scenario.Qa.Snapshot.SessionId!);
        await session.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(0, scenario.Memory.Reads); Assert.AreEqual(0, scenario.Memory.Writes);
        string valid = JsonSerializer.Serialize(scenario.Qa.Snapshot, QaWire.Json);
        foreach (string json in new[] { valid[..^1] + ",\"ended\":false}", valid[..^1] + ",\"Ended\":false}",
            valid.Replace("\"ended\":false,", "", StringComparison.Ordinal) })
        {
            using var badScenario = new Scenario();
            using var raw = new RawJsonHandler(json);
            using var invalid = new RunnerQa(new Uri("https://synthetic.invalid"), new string('d', 64), raw);
            var bad = new RunnerSession(invalid, badScenario.Desktop, badScenario.Sync, scenario.Qa.Snapshot.RunId,
                scenario.Qa.Snapshot.CandidateSha, scenario.Qa.Snapshot.SessionId!);
            await bad.RunAsync(true, CancellationToken.None);
            Assert.AreEqual(0, badScenario.Memory.Reads); Assert.AreEqual(0, badScenario.Memory.Writes);
        }
    }

    [TestMethod]
    public async Task RestoredBodiesAreRejectedByProductPolicyAndRunnerFixtureFilter()
    {
        var memory = new FakeMemory(); using var guard = new ClipboardTakeover(memory);
        guard.Capture(CancellationToken.None); guard.Write([new(13, [1, 0, 0, 0])], CancellationToken.None);
        Assert.AreEqual(RestoreOutcome.Restored, guard.Restore(CancellationToken.None));
        int before = memory.BodyReads;
        Assert.IsTrue(memory.WithOpen(() => ClipboardUploadPolicy.IsDenied(memory), CancellationToken.None));
        Assert.AreEqual(before, memory.BodyReads, "Product policy must reject before fetching the restored body.");
        await using var desktop = new FixtureDesktop(() => Assert.Fail("No real clipboard operation expected."));
        await desktop.PrepareAsync(CancellationToken.None);
        Assert.IsFalse(desktop.IsFixturePayload(ClipboardPayload.Text("synthetic local backup")));
        var fixture = FixtureCatalog.Get("unicode-v1");
        Assert.IsTrue(desktop.IsFixturePayload(new(fixture.Kind, fixture.MimeType, fixture.Bytes)));
    }

    [TestMethod]
    public async Task StoppedSyncCannotStartAfterCleanupAndStopIsIdempotent()
    {
        await using var desktop = new FixtureDesktop(() => Assert.Fail("No clipboard or network work expected."));
        await using var sync = new RunnerSync(desktop, "https://synthetic.invalid", new string('d', 64), () => Assert.Fail());
        Task stop = sync.StopAsync(); await stop;
        Assert.AreSame(stop, sync.StopAsync());
        Assert.ThrowsExactly<OperationCanceledException>(() => sync.Start(CancellationToken.None));
    }

    [TestMethod]
    public async Task ActualQaHandshakeAndEndRunUseFakeClipboardOnly()
    {
        await using var server = await P5TlsHost.Start(qa: true);
        using var scenario = new Scenario();
        using var client = new HttpClient(server.Handler()) { BaseAddress = new(server.Origin) };
        var lease = server.Lease!;
        var command = new QaCommand(lease.RunId, lease.CandidateSha, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "EndRun", null);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/qa/v1/commands")
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(command, QaWire.Json)) };
        request.Headers.Authorization = new("Bearer", server.TokenA); request.Content.Headers.ContentType = new("application/json");
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        using var qa = new RunnerQa(new(server.Origin), server.TokenB, server.Handler());
        string runner = Guid.NewGuid().ToString();
        var snapshot = await qa.PollAsync(new(lease.RunId, lease.CandidateSha, runner), CancellationToken.None);
        Assert.AreEqual("runner", snapshot.Role); Assert.AreEqual(runner, snapshot.SessionId);
        Assert.AreEqual("ContinuityBridge.Qa.Sidecar", snapshot.Service); Assert.IsTrue(snapshot.RunnerReady);
        var session = new RunnerSession(qa, scenario.Desktop, scenario.Sync, lease.RunId, lease.CandidateSha, runner);
        var result = await session.RunAsync(true, CancellationToken.None);
        Assert.AreEqual(RestoreOutcome.Restored, result.Restore); Assert.AreEqual(2, scenario.Memory.Writes);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
    }

    private sealed class RawJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class Scenario : IDisposable
    {
        internal FakeMemory Memory { get; } = new();
        internal FakeTime Time { get; } = new();
        internal FakeQa Qa { get; }
        internal FakeDesktop Desktop { get; }
        internal FakeSync Sync { get; } = new();
        internal RunnerSession Session { get; }
        internal Scenario(bool shortTimeout = false)
        {
            string run = Guid.NewGuid().ToString(), session = Guid.NewGuid().ToString(), sha = new('a', 40);
            Qa = new(new(run, sha, Time.GetUtcNow().AddMinutes(5), true, false,
                new(run, sha, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "EndRun", null), null,
                "ContinuityBridge.Qa.Sidecar", "runner", session));
            Desktop = new(Memory, Sync);
            Session = new(Qa, Desktop, Sync, run, sha, session, timeProvider: Time)
            { DrainTimeout = shortTimeout ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromSeconds(5),
                RestoreTimeout = shortTimeout ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromSeconds(2) };
        }
        public void Dispose() => Desktop.Guard.Dispose();
    }

    private sealed class FakeTime : TimeProvider
    {
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        private TimeSpan elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => start + elapsed;
        internal void Advance(TimeSpan value) => elapsed += value;
    }

    private sealed class FakeQa(QaSnapshot snapshot) : IRunnerQa
    {
        internal QaSnapshot Snapshot = snapshot;
        internal Func<QaSnapshot, QaSnapshot>? Mutate;
        internal bool Fail;
        internal bool BadAck;
        internal int Polls;
        internal List<QaResult> Results { get; } = [];
        public Task<QaSnapshot> PollAsync(QaPoll poll, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Polls++; if (Fail) throw new HttpRequestException(); return Task.FromResult(Mutate?.Invoke(Snapshot) ?? Snapshot); }
        public Task<QaSnapshot> ResultAsync(QaResult result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (Fail) throw new HttpRequestException(); Results.Add(result);
            return Task.FromResult(Snapshot with { Ended = true, Result = BadAck ? "MISMATCH" : result.Result });
        }
        public void Dispose() { }
    }

    private sealed class FakeSync : IRunnerSync
    {
        internal bool Stopped;
        internal Action? OnStart;
        internal bool FailStop;
        public void Start(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); OnStart?.Invoke(); }
        public Task StopAsync() { if (FailStop) throw new IOException(); Stopped = true; return Task.CompletedTask; }
    }

    private sealed class FakeDesktop(FakeMemory memory, FakeSync sync) : IRunnerDesktop
    {
        internal ClipboardTakeover Guard { get; } = new(memory);
        internal Action? AfterCapture;
        internal Func<CancellationToken, Task>? BeforeCommand;
        internal Func<CancellationToken, Task>? BeforeRestore;
        internal bool RestoreCalled;
        internal bool RestoredAfterSyncStop;
        public Task PrepareAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task CaptureAsync(CancellationToken cancellationToken) { Guard.Capture(cancellationToken); AfterCapture?.Invoke(); return Task.CompletedTask; }
        public async Task<bool> SetAsync(string fixture, CancellationToken cancellationToken)
        {
            if (fixture != "sentinel-v1" && BeforeCommand is not null) await BeforeCommand(cancellationToken);
            return Guard.Write([new(13, Encoding.Unicode.GetBytes(fixture + '\0'))], cancellationToken);
        }
        public Task<bool> VerifyAsync(string fixture, CancellationToken cancellationToken) => Task.FromResult(Guard.IsUnchanged());
        public async Task<RestoreOutcome> RestoreAsync(CancellationToken cancellationToken)
        {
            RestoreCalled = true; RestoredAfterSyncStop = sync.Stopped;
            if (BeforeRestore is not null) await BeforeRestore(cancellationToken);
            return Guard.Restore(cancellationToken);
        }
        public ValueTask DisposeAsync() { Guard.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeMemory : IClipboardMemory
    {
        private readonly Dictionary<string, uint> names = [];
        private bool opened;
        internal Dictionary<uint, byte[]> Data { get; } = new() { [13] = Encoding.Unicode.GetBytes("synthetic local backup\0") };
        public uint Sequence { get; set; } = 42;
        public bool IsOwner { get; private set; }
        internal int Reads;
        internal int BodyReads;
        internal int Writes;
        internal bool FailOpen;
        internal bool FailRead;
        internal bool FailPublish;
        internal bool Oversize;
        internal Action? BeforeNextOpen;
        internal Action? AfterCopy;
        internal IReadOnlyList<uint>? Advertised;
        internal bool UploadDenied => Data.TryGetValue(Format(Win32Clipboard.UploadToCloudFormatName), out var value) && value.SequenceEqual(new byte[4]);
        internal bool HistoryDenied => Data.TryGetValue(Format(Win32Clipboard.IncludeInHistoryFormatName), out var value) && value.SequenceEqual(new byte[4]);
        public uint Format(string name)
        { if (!names.TryGetValue(name, out uint value)) names[name] = value = (uint)(49152 + names.Count); return value; }
        public bool Contains(uint format) { Assert.IsTrue(opened); return Data.ContainsKey(format); }
        public IReadOnlyList<uint> EnumerateFormats() { Assert.IsTrue(opened); return Advertised ?? Data.Keys.ToArray(); }
        public byte[] Copy(uint format, int maximumBytes)
        {
            Assert.IsTrue(opened); Reads++; if (format == 13) BodyReads++;
            if (FailRead) throw new IOException();
            if (Oversize || Data[format].Length > maximumBytes) throw new InvalidDataException();
            byte[] copy = Data[format].ToArray(); AfterCopy?.Invoke(); return copy;
        }
        public T WithOpen<T>(Func<T> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (FailOpen) throw new IOException();
            var before = BeforeNextOpen; BeforeNextOpen = null; before?.Invoke();
            Assert.IsFalse(opened); opened = true; try { return action(); } finally { opened = false; }
        }
        public IClipboardPublication Prepare(IReadOnlyList<ClipboardMemoryEntry> entries)
        { Assert.IsFalse(opened); return new Publication(this, entries); }
        internal void ExternalCopy(string text)
        { Assert.IsFalse(opened); Sequence++; IsOwner = false; Data.Clear(); Data[13] = Encoding.Unicode.GetBytes(text + '\0'); }
        private sealed class Publication(FakeMemory memory, IReadOnlyList<ClipboardMemoryEntry> source) : IClipboardPublication
        {
            private readonly ClipboardMemoryEntry[] entries = source.Select(e => new ClipboardMemoryEntry(e.Format, e.Bytes.ToArray())).ToArray();
            public void Publish(Action emptied, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.IsTrue(memory.opened); memory.Writes++; memory.Sequence++; memory.IsOwner = true; memory.Data.Clear(); emptied();
                if (memory.FailPublish) throw new IOException();
                foreach (var entry in entries) memory.Data[entry.Format] = entry.Bytes.ToArray();
            }
            public void Dispose() { }
        }
    }
}
