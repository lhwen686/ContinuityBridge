using System.Security.Cryptography;
using System.Text;
using ContinuityBridge.Contracts;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Windows;

namespace ContinuityBridge.TestAgent;

// Native work is serialized on a dedicated STA, not the visible UI thread.
// External sequence changes stop the run without reading the new body.
internal sealed class FixtureDesktop : IRunnerDesktop, ICloudClipboard
{
    private static readonly Limits OracleLimits = new(1_000_000, 8_000_000, 20_000_001, 16_000_000);
    private readonly WindowsClipboardAdapter host;
    private readonly Action stop;
    private ClipboardTakeover? takeover;
    private readonly Dictionary<string, (string Kind, int Length, byte[] Hash)> fingerprints = [];
    private readonly Dictionary<string, QaFixture> fixtures = [];
    private readonly string fixtureDirectory = Path.Combine(Path.GetTempPath(), "ContinuityBridge-QA", Guid.NewGuid().ToString("N"));
    private string? fileFixturePath;
    private long generation;
    private bool syncStarted;
    private bool finishing;
    private string? lastFixture;

    internal FixtureDesktop(Action stop)
    {
        this.stop = stop;
        host = new WindowsClipboardAdapter(_ =>
        {
            if (!finishing && takeover is not null && !takeover.IsUnchanged()) stop();
        }, _ => stop(), cloudMode: true);
    }

    public long Generation => Interlocked.Read(ref generation);
    public event Action<long>? Changed;
    public Task PrepareAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        foreach (string id in FixtureCatalog.Ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fixture = FixtureCatalog.Get(id);
            fixtures[id] = fixture;
            fingerprints[id] = (fixture.Kind, fixture.Bytes.Length, SHA256.HashData(fixture.Bytes));
        }
    }, cancellationToken);

    public Task CaptureAsync(CancellationToken cancellationToken) => host.InvokeOnClipboardThreadAsync(native =>
    {
        takeover = new(native); takeover.Capture(cancellationToken); return true;
    }, cancellationToken);

    public async Task<bool> SetAsync(string fixtureId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fixture = fixtures[fixtureId];
        IReadOnlyList<PreparedClipboard> formats;
        if (fixtureId == "file-drop-v1")
        {
            Directory.CreateDirectory(fixtureDirectory);
            fileFixturePath = Path.Combine(fixtureDirectory, "fixture.txt");
            await File.WriteAllBytesAsync(fileFixturePath, fixture.Bytes, cancellationToken);
            byte[] pathBytes = Encoding.Unicode.GetBytes(fileFixturePath + "\0\0");
            byte[] drop = new byte[20 + pathBytes.Length]; drop[0] = 20; drop[16] = 1; pathBytes.CopyTo(drop, 20);
            formats = [new("files", drop)];
        }
        else if (fixture.Kind == "text")
            formats = [new("unicode", Encoding.Unicode.GetBytes(ClipboardPayload.StrictUtf8.GetString(fixture.Bytes) + '\0'))];
        else if (fixtureId == "bitmap-v1")
            formats = await Task.Run(() => new[] { new PreparedClipboard("dib", ImageClipboardCodec.Dib(
                ImageClipboardCodec.Decode(fixture.Bytes, fixture.MimeType, OracleLimits.MaxDecodedPixels), false)) }, cancellationToken);
        else formats = [new(fixture.MimeType == "image/png" ? "PNG" : "JFIF", fixture.Bytes)];
        return await host.InvokeOnClipboardThreadAsync(native =>
        {
            bool written = Write(native, formats, cancellationToken);
            if (written)
            {
                lastFixture = fixtureId;
                long value = Interlocked.Increment(ref generation);
                if (syncStarted) Changed?.Invoke(value);
            }
            return written;
        }, cancellationToken);
    }

    private bool Write(Win32Clipboard native, IReadOnlyList<PreparedClipboard> formats, CancellationToken cancellationToken, bool remote = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (finishing || takeover is null) return false;
        IClipboardMemory memory = native;
        var entries = new List<ClipboardMemoryEntry>();
        if (remote)
        {
            entries.Add(new(memory.Format(Win32Clipboard.IncludeInHistoryFormatName), new byte[4]));
            entries.Add(new(memory.Format(Win32Clipboard.UploadToCloudFormatName), new byte[4]));
        }
        foreach (var entry in formats)
            entries.Add(new(entry.Format switch { "unicode" => 13, "dib" => 8, "dibv5" => 17, "files" => 15, _ => memory.Format(entry.Format) }, entry.Bytes));
        if (takeover.Write(entries, cancellationToken)) return true;
        stop(); return false;
    }

    public async Task<bool> VerifyAsync(string fixtureId, CancellationToken cancellationToken)
    {
        if (fixtureId == "file-drop-v1")
        {
            return await host.InvokeOnClipboardThreadAsync(native =>
            {
                if (finishing || takeover?.IsUnchanged() != true || lastFixture != fixtureId || fileFixturePath is null) return false;
                IClipboardMemory memory = native;
                return memory.WithOpen(() =>
                {
                    if (!takeover.IsUnchanged()) return false;
                    byte[] actual = memory.Copy(15, 4096);
                    byte[] expectedPath = Encoding.Unicode.GetBytes(fileFixturePath + "\0\0");
                    return actual.Length >= 20 && actual.AsSpan(20).SequenceEqual(expectedPath);
                }, cancellationToken);
            }, cancellationToken);
        }
        var current = await ReadAsync(cancellationToken);
        return current is not null && await Task.Run(() => Matches(current, fixtureId), cancellationToken);
    }

    private async Task<ClipboardPayload?> ReadAsync(CancellationToken cancellationToken)
    {
        var raw = await host.InvokeOnClipboardThreadAsync(native =>
        {
            if (finishing || takeover?.IsUnchanged() != true) { stop(); return null; }
            return native.ReadCloudRaw(takeover.ExpectedSequence, OracleLimits, cancellationToken, fixtureOracle: true);
        }, cancellationToken);
        return raw is null ? null : await Task.Run(() => ImageClipboardCodec.ToPayload(raw, OracleLimits, cancellationToken), cancellationToken);
    }

    private bool Matches(ClipboardPayload current, string fixtureId)
    {
        var expected = fingerprints[fixtureId];
        if (current.Kind == expected.Kind && current.Bytes.Length == expected.Length &&
            SHA256.HashData(current.Bytes).AsSpan().SequenceEqual(expected.Hash)) return true;
        if (current.Kind != "image" || fixtureId is not ("alpha-png-v1" or "jpeg-v1" or "bitmap-v1")) return false;
        var fixture = fixtures[fixtureId];
        var actual = ImageClipboardCodec.Decode(current.Bytes, current.MimeType, OracleLimits.MaxDecodedPixels);
        var pixels = ImageClipboardCodec.Decode(fixture.Bytes, fixture.MimeType, OracleLimits.MaxDecodedPixels);
        if (fixtureId == "bitmap-v1")
            pixels = ImageClipboardCodec.DecodeDib(ImageClipboardCodec.Dib(pixels, false), OracleLimits.MaxDecodedPixels, CancellationToken.None);
        return actual.Width == pixels.Width && actual.Height == pixels.Height && actual.Bgra.AsSpan().SequenceEqual(pixels.Bgra);
    }

    internal bool IsFixturePayload(ClipboardPayload payload) => fingerprints.Keys.Any(id => Matches(payload, id));

    public Task StartAsync(CancellationToken cancellationToken) => host.InvokeOnClipboardThreadAsync(_ =>
    { cancellationToken.ThrowIfCancellationRequested(); syncStarted = true; return true; }, cancellationToken);

    public async Task<ClipboardPayload?> CaptureAsync(long expected, Limits limits, CancellationToken cancellationToken)
    {
        if (expected != Generation) return null;
        var body = await ReadAsync(cancellationToken);
        if (body is null || expected != Generation) return null;
        bool known = await Task.Run(() => IsFixturePayload(body), cancellationToken);
        if (!known) { stop(); return null; }
        body.Validate(limits); return body;
    }

    public async Task<bool> TryApplyAsync(ClipboardPayload payload, long expected, Limits limits, CancellationToken cancellationToken)
    {
        if (!await Task.Run(() => IsFixturePayload(payload), cancellationToken)) { stop(); return false; }
        var formats = await Task.Run(() => ImageClipboardCodec.Prepare(payload, limits, cancellationToken), cancellationToken);
        return await host.InvokeOnClipboardThreadAsync(native => expected == Generation && Write(native, formats, cancellationToken, remote: true), cancellationToken);
    }

    public Task<RestoreOutcome> RestoreAsync(CancellationToken cancellationToken)
    {
        if (takeover is null) return Task.FromResult(RestoreOutcome.Untouched);
        return host.InvokeOnClipboardThreadAsync(_ =>
        { finishing = true; return takeover.Restore(cancellationToken); }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync(); takeover?.Dispose();
        if (fileFixturePath is not null)
            try { File.Delete(fileFixturePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
