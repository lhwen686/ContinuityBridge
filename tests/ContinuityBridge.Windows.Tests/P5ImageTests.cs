using System.Buffers.Binary;
using ContinuityBridge.Contracts;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
[TestCategory("P5")]
public sealed class P5ImageTests
{
    internal static readonly Limits Limits = new(1_000_000, 8_000_000, 20_000_000, 64_000_000);

    [TestMethod]
    public void SyntheticCredentialRoundTripIsScopedToOneOsProtectedTarget()
    {
        string origin = "https://fixture-" + Guid.NewGuid().ToString("N") + ".example.invalid";
        string token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        try
        {
            Assert.IsNull(DeviceCredentialStore.Read(origin));
            DeviceCredentialStore.Save(origin, token);
            bool matches = DeviceCredentialStore.Read(origin) == token;
            Assert.IsTrue(matches, "Synthetic OS credential roundtrip mismatch.");
        }
        finally { DeviceCredentialStore.Delete(origin); }
        Assert.IsNull(DeviceCredentialStore.Read(origin));
    }

    [TestMethod]
    public void PngPreservesEncodedBytesAndDibV5PreservesEveryPixel()
    {
        var fixture = FixtureCatalog.Get("alpha-png-v1");
        var payload = ImageClipboardCodec.ToPayload(new("png", fixture.Bytes), Limits, CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Bytes, payload.Bytes);
        var pixels = ImageClipboardCodec.Decode(payload.Bytes, payload.MimeType, Limits.MaxDecodedPixels);
        var formats = ImageClipboardCodec.Prepare(payload, Limits, CancellationToken.None);
        var dib = formats.Single(f => f.Format == "dibv5").Bytes;
        Assert.AreEqual(-pixels.Height, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        var roundTrip = ImageClipboardCodec.DecodeDib(dib, Limits.MaxDecodedPixels, CancellationToken.None);
        CollectionAssert.AreEqual(pixels.Bgra, roundTrip.Bgra);
        // Asymmetric source rows distinguish vertical inversion from a visually symmetric image.
        Assert.AreNotEqual(pixels.Bgra[1], pixels.Bgra[pixels.Width * 4 + 1]);
        Assert.IsTrue(pixels.Bgra.Where((_, i) => i % 4 == 3).Contains((byte)0));
        Assert.IsTrue(pixels.Bgra.Where((_, i) => i % 4 == 3).Contains((byte)170));
    }

    [TestMethod]
    public void EveryCommittedFixtureMatchesItsExportedLengthAndHash()
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            P5TlsHost.FindRoot(), "docs", "cloud-clipboard-v1", "windows", "fixtures.json")));
        var entries = manifest.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        Assert.HasCount(FixtureCatalog.Ids.Count, entries);
        foreach (var entry in entries)
        {
            var fixture = FixtureCatalog.Get(entry.GetProperty("fixtureId").GetString()!);
            Assert.HasCount(entry.GetProperty("byteLength").GetInt32(), fixture.Bytes);
            Assert.AreEqual(entry.GetProperty("sha256").GetString(),
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fixture.Bytes)));
        }
    }

    [TestMethod]
    public void BitmapOnlyUsesPngAndLegacyDibHasDefinedWhiteComposite()
    {
        var fixture = FixtureCatalog.Get("alpha-png-v1");
        var pixels = ImageClipboardCodec.Decode(fixture.Bytes, fixture.MimeType, Limits.MaxDecodedPixels);
        byte[] dib = ImageClipboardCodec.Dib(pixels, false);
        var actual = ImageClipboardCodec.ToPayload(new("dib", dib), Limits, CancellationToken.None);
        Assert.AreEqual("image/png", actual.MimeType);
        var decoded = ImageClipboardCodec.Decode(actual.Bytes, actual.MimeType, Limits.MaxDecodedPixels);
        Assert.AreEqual((byte)255, decoded.Bgra[3]); Assert.AreEqual((byte)255, decoded.Bgra[0]);
        CollectionAssert.AreEqual(ImageClipboardCodec.DecodeDib(dib, Limits.MaxDecodedPixels, CancellationToken.None).Bgra, decoded.Bgra);
    }

    [TestMethod]
    public void BottomUpDibIsUprightAndPaletteWorks()
    {
        byte[] dib = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2); dib[12] = 1; dib[14] = 8; dib[32] = 2;
        dib[42] = 255; dib[45] = 255; // palette red, green
        dib[48] = 1; dib[49] = 0; dib[52] = 0; dib[53] = 1;
        var decoded = ImageClipboardCodec.DecodeDib(dib, Limits.MaxDecodedPixels, CancellationToken.None);
        Assert.AreEqual((byte)255, decoded.Bgra[2]); Assert.AreEqual((byte)255, decoded.Bgra[9]);
    }

    [TestMethod]
    public void JpegBytesArePreservedAndAllExifOrientationsMapDistinctCorners()
    {
        var fixture = FixtureCatalog.Get("jpeg-v1");
        var payload = ImageClipboardCodec.ToPayload(new("jpeg", fixture.Bytes), Limits, CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Bytes, payload.Bytes);
        byte[] source = [1, 0, 0, 255, 2, 0, 0, 255, 3, 0, 0, 255, 4, 0, 0, 255, 5, 0, 0, 255, 6, 0, 0, 255];
        byte[][] expected = [[1,2,3,4,5,6], [3,2,1,6,5,4], [6,5,4,3,2,1], [4,5,6,1,2,3],
            [1,4,2,5,3,6], [4,1,5,2,6,3], [6,3,5,2,4,1], [3,6,2,5,1,4]];
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            var rotated = ImageClipboardCodec.Orient(new(3, 2, source), orientation);
            CollectionAssert.AreEqual(expected[orientation - 1], rotated.Bgra.Where((_, i) => i % 4 == 0).ToArray());
            Assert.AreEqual(orientation >= 5 ? 2 : 3, rotated.Width);
        }
    }

    [TestMethod]
    public void MalformedImagesAndDecodedPixelBombsAreRejectedBeforeClipboardMutation()
    {
        byte[] dib = new byte[40]; dib[0] = 40; dib[12] = 1; dib[14] = 32;
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 100000); BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 100000);
        Assert.ThrowsExactly<InvalidDataException>(() => ImageClipboardCodec.ToPayload(new("dib", dib), Limits, CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() => ImageClipboardCodec.ToPayload(new("png", [1,2,3]), Limits, CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() => ClipboardPayload.Text("nul\0fixture"));
    }

    [TestMethod]
    public void ExactDecimal20MbAndOverLimitUseLegalPngChunks()
    {
        var accepted = FixtureCatalog.Get("png-20000000-v1");
        Assert.HasCount(20_000_000, accepted.Bytes);
        var payload = ImageClipboardCodec.ToPayload(new("png", accepted.Bytes), Limits, CancellationToken.None);
        CollectionAssert.AreEqual(accepted.Bytes, payload.Bytes);
        var rejected = FixtureCatalog.Get("png-20000001-v1");
        Assert.HasCount(20_000_001, rejected.Bytes);
        Assert.ThrowsExactly<InvalidDataException>(() => ImageClipboardCodec.ToPayload(new("png", rejected.Bytes), Limits, CancellationToken.None));
    }
}
