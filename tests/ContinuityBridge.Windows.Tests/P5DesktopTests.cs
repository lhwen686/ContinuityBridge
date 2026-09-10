using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContinuityBridge.Windows.Tests;

[TestClass]
[TestCategory("P5Desktop")]
public sealed class P5DesktopTests
{
    [TestMethod]
    public async Task VisibleNativeCopyHttpsSyncImagePastePrivacyAndPause()
    {
        if (Environment.GetEnvironmentVariable("CB_P5_DESKTOP_FIXTURES") != "1")
            Assert.Inconclusive("NOT RUN: explicit P5 synthetic desktop test window required.");
        Assert.IsTrue(Environment.UserInteractive);
        Assert.AreNotEqual(0, Process.GetCurrentProcess().SessionId);
        await using var host = await P5TlsHost.Start();
        await StaTestThread.RunAsync(() => RunDesktop(host));
    }

    private static void RunDesktop(P5TlsHost host)
    {
        using var mutex = new Mutex(true, @"Local\ContinuityBridge.Qa.Desktop", out bool owned);
        Assert.IsTrue(owned, "Another desktop QA session is active.");
        Exception? failure = null;
        bool finished = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var form = new Form { Text = "ContinuityBridge P5 · 合成剪贴板测试窗口", ClientSize = new Size(780, 580), StartPosition = FormStartPosition.CenterScreen };
        var progress = new Label { AutoSize = true, Text = "只测试本窗口内的合成内容；可随时停止。" };
        var source = new TextBox { Multiline = true, Width = 720, Height = 100, MaxLength = 1_000_000 };
        var target = new TextBox { Multiline = true, Width = 720, Height = 100, MaxLength = 1_000_000 };
        var imageTarget = new RichTextBox { Width = 720, Height = 150 };
        var stop = new Button { Text = "停止测试", AutoSize = true };
        stop.Click += (_, _) => cancellation.Cancel();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(progress); panel.Controls.Add(new Label { Text = "真实源编辑控件", AutoSize = true }); panel.Controls.Add(source);
        panel.Controls.Add(new Label { Text = "真实目标编辑控件 / 图片粘贴", AutoSize = true }); panel.Controls.Add(target); panel.Controls.Add(imageTarget); panel.Controls.Add(stop);
        form.Controls.Add(panel);
        form.FormClosing += (_, e) => { if (!finished) { e.Cancel = true; cancellation.Cancel(); } };
        Task? scenario = null;
        form.Shown += (_, _) => scenario = RunScenarioAsync();
        async Task RunScenarioAsync()
        {
            CloudSyncEngine? engine = null;
            Task? sync = null;
            using var productLife = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            var adapterErrors = new ConcurrentBag<string>();
            await using var adapter = new CloudClipboardAdapter(ex => adapterErrors.Add(ex.GetType().Name + ":" + ex.Message));
            using var windows = new CloudTransport(host.Origin, host.TokenA, host.Handler());
            using var phone = new CloudTransport(host.Origin, host.TokenB, host.Handler());
            var statuses = new ConcurrentBag<SyncStatus>();
            try
            {
                // Authorized initial takeover, without reading the user's old clipboard.
                source.Text = "CB P5 pre-start synthetic sentinel"; source.SelectAll(); source.Copy();
                engine = new(adapter, windows, pollInterval: TimeSpan.FromMilliseconds(50));
                engine.StatusChanged += statuses.Add; sync = Task.Run(() => engine.RunAsync(productLife.Token));
                await Until(() => Task.FromResult(statuses.Contains(SyncStatus.Polling)), cancellation.Token);
                Assert.IsNull((await phone.StateAsync(cancellation.Token)).Item);

                progress.Text = "真实文本复制 → HTTPS → 读回完整性";
                string text = Encoding.UTF8.GetString(FixtureCatalog.Get("long-text-v1").Bytes);
                source.Text = text; source.SelectAll(); source.Copy();
                await Until(async () => (await phone.StateAsync(cancellation.Token)).Item?.SourceDeviceId == "windows", cancellation.Token);
                var local = await phone.StateAsync(cancellation.Token);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(text), (await phone.DownloadAsync(local.Item!, P5ImageTests.Limits, cancellation.Token)).Bytes);

                progress.Text = "HTTPS 远端文本 → 真实剪贴板 → 目标控件粘贴";
                string remoteText = "  API→真实 Windows 合成 🧪\r\n末尾空格  ";
                var remote = await phone.PutAsync(ClipboardPayload.Text(remoteText), local.Etag, Guid.NewGuid(), cancellation.Token);
                await Until(() => Task.FromResult(statuses.Contains(SyncStatus.RemoteApplied)), cancellation.Token);
                target.Clear(); target.Paste(); Assert.AreEqual(remoteText, target.Text);
                await Task.Delay(200, cancellation.Token);
                Assert.AreEqual(remote.State.Etag, (await phone.StateAsync(cancellation.Token)).Etag);

                foreach (string fixtureId in new[] { "alpha-png-v1", "jpeg-v1" })
                {
                    progress.Text = fixtureId + "：真实图片源 → 上传 → 远端写回 → RichEdit 图片粘贴";
                    var fixture = FixtureCatalog.Get(fixtureId);
                    var native = new Win32Clipboard(form.Handle);
                    Assert.IsTrue(native.TryWriteCloud([new(fixture.MimeType == "image/png" ? "PNG" : "JFIF", fixture.Bytes)],
                        NativeMethods.GetClipboardSequenceNumber(), Guid.Empty, cancellation.Token, fixtureSource: true));
                    string prior = (await phone.StateAsync(cancellation.Token)).Etag;
                    await Until(async () => (await phone.StateAsync(cancellation.Token)).Item is { SourceDeviceId: "windows", Kind: "image" } item && item.MimeType == fixture.MimeType,
                        cancellation.Token);
                    var uploaded = await phone.StateAsync(cancellation.Token);
                    CollectionAssert.AreEqual(fixture.Bytes, (await phone.DownloadAsync(uploaded.Item!, P5ImageTests.Limits, cancellation.Token)).Bytes);
                    int applyCount = statuses.Count(s => s == SyncStatus.RemoteApplied);
                    var sent = await phone.PutAsync(new(fixture.Kind, fixture.MimeType, fixture.Bytes), uploaded.Etag, Guid.NewGuid(), cancellation.Token);
                    await Until(() => Task.FromResult(statuses.Count(s => s == SyncStatus.RemoteApplied) > applyCount), cancellation.Token);
                    Assert.IsTrue(NativeMethods.IsClipboardFormatAvailable(NativeMethods.RegisterClipboardFormat("PNG")));
                    Assert.IsTrue(NativeMethods.IsClipboardFormatAvailable(17)); Assert.IsTrue(NativeMethods.IsClipboardFormatAvailable(8));
                    imageTarget.Clear(); imageTarget.Paste();
                    string pastedRtf = imageTarget.Rtf ?? "";
                    Assert.IsTrue(pastedRtf.Contains("\\pict", StringComparison.Ordinal) || pastedRtf.Contains("\\object", StringComparison.Ordinal), "RichEdit did not accept a real image.");
                    var raw = native.ReadCloudRaw(NativeMethods.GetClipboardSequenceNumber(), P5ImageTests.Limits, cancellation.Token, fixtureOracle: true)!;
                    var actual = ImageClipboardCodec.Decode(raw.Bytes, "image/png", P5ImageTests.Limits.MaxDecodedPixels);
                    var expected = ImageClipboardCodec.Decode(fixture.Bytes, fixture.MimeType, P5ImageTests.Limits.MaxDecodedPixels);
                    CollectionAssert.AreEqual(expected.Bgra, actual.Bgra);
                    await Task.Delay(150, cancellation.Token); Assert.AreEqual(sent.State.Etag, (await phone.StateAsync(cancellation.Token)).Etag);
                }

                progress.Text = "CF_BITMAP 源、普通文件、显式私密格式";
                using (var bitmap = new Bitmap(7, 5))
                {
                    using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.Blue); graphics.FillRectangle(Brushes.Red, 0, 0, 3, 2); }
                    Clipboard.SetImage(bitmap);
                }
                var bitmapRaw = new Win32Clipboard(form.Handle).ReadCloudRaw(NativeMethods.GetClipboardSequenceNumber(), P5ImageTests.Limits, cancellation.Token, fixtureOracle: true)!;
                File.WriteAllBytes(Path.Combine(P5TlsHost.FindRoot(), "artifacts", "p5", "bitmap-source." + bitmapRaw.Format), bitmapRaw.Bytes);
                var previous = await phone.StateAsync(cancellation.Token);
                await Until(async () => (await phone.StateAsync(cancellation.Token)).Item?.SourceDeviceId == "windows", cancellation.Token);
                var bitmapState = await phone.StateAsync(cancellation.Token);
                var bitmapBody = await phone.DownloadAsync(bitmapState.Item!, P5ImageTests.Limits, cancellation.Token);
                File.WriteAllBytes(Path.Combine(P5TlsHost.FindRoot(), "artifacts", "p5", "bitmap-upload.png"), bitmapBody.Bytes);
                Assert.AreEqual("image/png", bitmapBody.MimeType);
                var bitmapPixels = ImageClipboardCodec.Decode(bitmapBody.Bytes, bitmapBody.MimeType, P5ImageTests.Limits.MaxDecodedPixels);
                Assert.AreEqual(7, bitmapPixels.Width); Assert.AreEqual(5, bitmapPixels.Height);
                Assert.AreEqual((byte)255, bitmapPixels.Bgra[2]); Assert.AreEqual((byte)255, bitmapPixels.Bgra[(4 * 7 + 6) * 4]);
                string path = Path.Combine(P5TlsHost.FindRoot(), "artifacts", "p5", "fixed-file-fixture.txt"); File.WriteAllText(path, "synthetic file; never upload");
                Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                await Task.Delay(200, cancellation.Token); Assert.AreEqual(bitmapState.Etag, (await phone.StateAsync(cancellation.Token)).Etag);
                byte[] filePath = Encoding.Unicode.GetBytes(path + "\0\0");
                byte[] dropWithPreview = new byte[20 + filePath.Length]; dropWithPreview[0] = 20; dropWithPreview[16] = 1; filePath.CopyTo(dropWithPreview, 20);
                var mixedFileSource = new Win32Clipboard(form.Handle);
                Assert.IsTrue(mixedFileSource.TryWriteCloud([new("files", dropWithPreview), new("PNG", FixtureCatalog.Get("alpha-png-v1").Bytes)],
                    NativeMethods.GetClipboardSequenceNumber(), Guid.Empty, cancellation.Token, fixtureSource: true));
                await Task.Delay(200, cancellation.Token); Assert.AreEqual(bitmapState.Etag, (await phone.StateAsync(cancellation.Token)).Etag);
                await InteractiveClipboardScope.SetTextWithMarkersAsync("private synthetic fixture", uploadToCloud: 0);
                await Task.Delay(200, cancellation.Token); Assert.AreEqual(bitmapState.Etag, (await phone.StateAsync(cancellation.Token)).Etag);

                progress.Text = "旧下载原子失效、清空云端不清本地、暂停/恢复";
                long oldGeneration = adapter.Generation;
                source.Text = "newer local fixture";
                var nativeSource = new Win32Clipboard(form.Handle);
                Assert.IsTrue(nativeSource.TryWriteCloud([new("unicode", Encoding.Unicode.GetBytes(source.Text + '\0'))], NativeMethods.GetClipboardSequenceNumber(), Guid.Empty, cancellation.Token, fixtureSource: true));
                Assert.IsFalse(await adapter.TryApplyAsync(ClipboardPayload.Text("stale remote fixture"), oldGeneration, P5ImageTests.Limits, cancellation.Token));
                await Until(async () => (await phone.StateAsync(cancellation.Token)).Etag != bitmapState.Etag, cancellation.Token);
                engine.RequestClear(); await Until(() => Task.FromResult(statuses.Contains(SyncStatus.CloudCleared)), cancellation.Token);
                target.Clear(); target.Paste(); Assert.AreEqual(source.Text, target.Text);
                await productLife.CancelAsync(); await sync;
                source.Text = "CB P5 finished synthetic sentinel"; source.SelectAll(); source.Copy();
                await Task.Delay(200, cancellation.Token); Assert.IsNull((await phone.StateAsync(cancellation.Token)).Item);

                progress.Text = "本次真实 Windows 窗口检查完成；iPhone E2E 尚未运行。";
                // Only this owned test window is captured; it contains synthetic fixtures.
                using var capture = new Bitmap(form.Width, form.Height); form.DrawToBitmap(capture, form.ClientRectangle);
                capture.Save(Path.Combine(P5TlsHost.FindRoot(), "artifacts", "p5", "desktop-target.png"));
                await Task.Delay(1200, cancellation.Token);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                await productLife.CancelAsync(); if (sync is not null) try { await sync; } catch (Exception ex) { adapterErrors.Add(ex.GetType().Name + ":" + ex.Message); failure ??= ex; }
                File.WriteAllText(Path.Combine(P5TlsHost.FindRoot(), "artifacts", "p5", "desktop-status.json"), System.Text.Json.JsonSerializer.Serialize(new { statuses = statuses.Select(s => s.ToString()).ToArray(), errors = adapterErrors.ToArray(), generation = adapter.Generation }));
                await adapter.DisposeAsync(); finished = true; form.Close();
            }
        }
        Application.Run(form);
        scenario?.GetAwaiter().GetResult();
        mutex.ReleaseMutex();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task Until(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!await predicate()) await Task.Delay(20, deadline.Token);
    }
}
