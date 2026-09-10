using System.Security.Cryptography;
using System.Text;
using ContinuityBridge.Contracts;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Windows;

namespace ContinuityBridge.TestAgent;

// This fixture-only desktop oracle is excluded from the product dependency graph.
internal sealed class FixtureDesktop
{
    private static readonly Limits Limits = new(1_000_000, 8_000_000, 20_000_001, 16_000_000);
    private readonly Win32Clipboard native;
    private readonly Dictionary<string, (string Kind, int Length, byte[] Hash)> fingerprints = [];
    private readonly Dictionary<string, QaFixture> fixtures = [];
    private readonly string fixtureDirectory = Path.Combine(Path.GetTempPath(), "ContinuityBridge-QA", Guid.NewGuid().ToString("N"));
    private string? fileFixturePath;

    internal FixtureDesktop(nint handle) => native = new(handle);
    internal void PrepareCatalog()
    {
        foreach (string id in FixtureCatalog.Ids)
        {
            var fixture = FixtureCatalog.Get(id);
            fixtures[id] = fixture;
            fingerprints[id] = (fixture.Kind, fixture.Bytes.Length, SHA256.HashData(fixture.Bytes));
        }
    }

    internal async Task<bool> IsKnownAsync(CancellationToken cancellationToken)
    {
        if (System.Windows.Forms.Clipboard.ContainsFileDropList())
        {
            var list = System.Windows.Forms.Clipboard.GetFileDropList();
            return fileFixturePath is not null && list.Count == 1 && list[0] == fileFixturePath;
        }
        var current = await ReadAsync(cancellationToken);
        if (current is null) return false;
        byte[] hash = SHA256.HashData(current.Bytes);
        return fingerprints.Values.Any(f => f.Kind == current.Kind && f.Length == current.Bytes.Length && f.Hash.AsSpan().SequenceEqual(hash)) ||
            await Task.Run(() => MatchPixels(current, "alpha-png-v1") || MatchPixels(current, "jpeg-v1") || MatchPixels(current, "bitmap-v1"), cancellationToken);
    }

    internal bool Set(string fixtureId, uint expectedSequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fixture = fixtures[fixtureId];
        if (fixtureId == "file-drop-v1")
        {
            Directory.CreateDirectory(fixtureDirectory); fileFixturePath = Path.Combine(fixtureDirectory, "fixture.txt");
            File.WriteAllBytes(fileFixturePath, fixture.Bytes);
            byte[] pathBytes = Encoding.Unicode.GetBytes(fileFixturePath + "\0\0");
            byte[] drop = new byte[20 + pathBytes.Length]; drop[0] = 20; drop[16] = 1; pathBytes.CopyTo(drop, 20);
            return Write([new("files", drop)]);
        }
        if (fixture.Kind == "text")
        {
            // Real native edit-control copy is also exercised in P5 desktop tests.
            return Write([new("unicode", Encoding.Unicode.GetBytes(ClipboardPayload.StrictUtf8.GetString(fixture.Bytes) + '\0'))]);
        }
        if (fixtureId == "bitmap-v1")
        {
            var pixels = ImageClipboardCodec.Decode(fixture.Bytes, fixture.MimeType, Limits.MaxDecodedPixels);
            return Write([new("dib", ImageClipboardCodec.Dib(pixels, false))]);
        }
        // Fixed encoded fixture data. Product capture must read real clipboard formats.
        return Write([new(fixture.MimeType == "image/png" ? "PNG" : "JFIF", fixture.Bytes)]);
        bool Write(IReadOnlyList<PreparedClipboard> formats) => native.TryWriteCloud(formats, expectedSequence, Guid.Empty, cancellationToken, fixtureSource: true);
    }

    internal async Task<bool> VerifyAsync(string fixtureId, CancellationToken cancellationToken)
    {
        if (fixtureId == "file-drop-v1")
        {
            if (fileFixturePath is null || !Clipboard.ContainsFileDropList()) return false;
            var list = Clipboard.GetFileDropList();
            return list.Count == 1 && list[0] == fileFixturePath;
        }
        var current = await ReadAsync(cancellationToken); if (current is null) return false;
        var expected = fingerprints[fixtureId];
        return current.Kind == expected.Kind && current.Bytes.Length == expected.Length &&
            SHA256.HashData(current.Bytes).AsSpan().SequenceEqual(expected.Hash) || await Task.Run(() => MatchPixels(current, fixtureId), cancellationToken);
    }

    private async Task<ClipboardPayload?> ReadAsync(CancellationToken cancellationToken)
    {
        // Contents stay in this process; the only output is a finite PASS/MISMATCH code.
        var raw = native.ReadCloudRaw(NativeMethods.GetClipboardSequenceNumber(), Limits, CancellationToken.None, fixtureOracle: true);
        return raw is null ? null : await Task.Run(() => ImageClipboardCodec.ToPayload(raw, Limits, cancellationToken), cancellationToken);
    }

    private static bool MatchPixels(ClipboardPayload current, string fixtureId)
    {
        if (current.Kind != "image" || fixtureId is not ("alpha-png-v1" or "jpeg-v1" or "bitmap-v1")) return false;
        var fixture = FixtureCatalog.Get(fixtureId);
        var actual = ImageClipboardCodec.Decode(current.Bytes, current.MimeType, Limits.MaxDecodedPixels);
        var expected = ImageClipboardCodec.Decode(fixture.Bytes, fixture.MimeType, Limits.MaxDecodedPixels);
        if (fixtureId == "bitmap-v1")
            expected = ImageClipboardCodec.DecodeDib(ImageClipboardCodec.Dib(expected, false), Limits.MaxDecodedPixels, CancellationToken.None);
        return actual.Width == expected.Width && actual.Height == expected.Height && actual.Bgra.AsSpan().SequenceEqual(expected.Bgra);
    }
}
