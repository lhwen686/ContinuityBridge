using System.Net.Http.Headers;
using System.Text.Json;
using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Windows;

namespace ContinuityBridge.TestAgent;

internal sealed class RunnerForm : Form
{
    private readonly TextBox origin = new() { Width = 700, AccessibleName = "QA HTTPS 地址", PlaceholderText = "QA staging HTTPS origin" };
    private readonly TextBox runId = new() { Width = 700, AccessibleName = "测试 runId", PlaceholderText = "短期 runId" };
    private readonly TextBox token = new() { Width = 700, AccessibleName = "Runner 凭据", UseSystemPasswordChar = true, PlaceholderText = "仅 runner token，不保存到磁盘" };
    private readonly Label status = new() { AutoSize = true, Text = "未启用；没有读取剪贴板或连接 QA。" };
    private readonly CheckBox consent = new() { AutoSize = true, Text = "允许本次租约用合成 fixture 覆盖剪贴板" };
    private readonly Button start = new() { Text = "开始测试", AutoSize = true };
    private readonly Button stop = new() { Text = "立即停止", AutoSize = true, Enabled = false };
    private CancellationTokenSource? lifetime;
    private Task? run;
    private FixtureDesktop? desktop;

    internal RunnerForm()
    {
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ContinuityBridge TestAgent · 可停止的合成测试"; ClientSize = new Size(780, 620); MinimumSize = new Size(800, 660);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        layout.Controls.Add(new Label { AutoSize = true, Text = "候选 SHA：" + CandidateBuild.Sha });
        layout.Controls.Add(origin); layout.Controls.Add(runId); layout.Controls.Add(token);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(700, 0), Text = "仅 staging QA。固定 fixture、单桌面会话、最长 45 分钟。发现非 fixture 内容会停止，不回传内容。结束后保留最后的合成剪贴板；原内容不备份、不上传。" });
        layout.Controls.Add(consent); layout.Controls.Add(start); layout.Controls.Add(stop); layout.Controls.Add(status); Controls.Add(layout);
        start.Click += async (_, _) => await StartRunAsync();
        stop.Click += (_, _) => lifetime?.Cancel();
        FormClosing += (_, e) =>
        {
            if (lifetime is not null) { e.Cancel = true; lifetime.Cancel(); status.Text = "正在停止，请稍后关闭窗口。"; }
        };
    }

    private async Task StartRunAsync()
    {
        if (run is { IsCompleted: false } || !consent.Checked) return;
        try
        {
            Uri uri = CloudTransport.ValidateOrigin(origin.Text.Trim());
            if (!QaWire.IsId(runId.Text.Trim()) || !QaWire.IsHash(token.Text.Trim()) || !QaWire.IsSha(CandidateBuild.Sha)) throw new InvalidDataException();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(45)); lifetime = cancellation;
            start.Enabled = false; stop.Enabled = true;
            origin.Enabled = false; runId.Enabled = false; token.Enabled = false;
            desktop = new FixtureDesktop(Handle);
            status.Text = "准备已提交的固定 fixture…";
            await Task.Run(desktop.PrepareCatalog, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            // Explicit visible opt-in authorizes this initial takeover; never read the old clipboard.
            if (!desktop.Set("sentinel-v1", NativeMethods.GetClipboardSequenceNumber(), cancellation.Token)) throw new InvalidDataException();
            string secret = token.Text.Trim(); token.Clear();
            run = RunAsync(uri, runId.Text.Trim(), secret, cancellation.Token);
            await run;
            status.Text = "测试会话已停止；没有后台 QA 控制。";
        }
        catch (Exception) { status.Text = "测试已停止：配置、租约、网络或 fixture 不匹配。没有回传剪贴板内容。"; }
        finally
        {
            lifetime = null; start.Enabled = true; stop.Enabled = false;
            origin.Enabled = true; runId.Enabled = true; token.Enabled = true; consent.Checked = false;
        }
    }

    private async Task RunAsync(Uri uri, string id, string secret, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        string session = Guid.NewGuid().ToString();
        var done = new Dictionary<string, string>();
        DateTimeOffset? expiry = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = await Post("/qa/v1/poll", new QaPoll(id, CandidateBuild.Sha, session));
            if (snapshot.RunId != id || snapshot.CandidateSha != CandidateBuild.Sha || snapshot.Ended) break;
            expiry ??= snapshot.ExpiresAt;
            if (snapshot.ExpiresAt != expiry || expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddMinutes(45)) break;
            status.Text = "测试已就绪 · " + (snapshot.Command?.Action ?? "等待固定动作");
            if (snapshot.Command is { } command && snapshot.Result is null)
            {
                if (command.RunId != id || command.CandidateSha != CandidateBuild.Sha || !QaWire.IsId(command.CommandId) ||
                    !QaWire.IsAction(command.Action, command.FixtureId)) break;
                if (!done.TryGetValue(command.CommandId, out string? result))
                {
                    if (done.Count >= 64) break;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    TimeSpan remaining = expiry.Value - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    deadline.CancelAfter(remaining < TimeSpan.FromSeconds(90) ? remaining : TimeSpan.FromSeconds(90));
                    try
                    {
                        uint expectedSequence = NativeMethods.GetClipboardSequenceNumber();
                        if (command.Action == "EndRun") result = "STOPPED";
                        else if (command.Action == "Status") result = "PASS";
                        else if (!await desktop!.IsKnownAsync(deadline.Token)) result = "MISMATCH";
                        else if (command.Action == "SetFixtureClipboard") result = desktop.Set(command.FixtureId!, expectedSequence, deadline.Token) ? "PASS" : "MISMATCH";
                        else result = await desktop.VerifyAsync(command.FixtureId!, deadline.Token) ? "PASS" : "MISMATCH";
                        deadline.Token.ThrowIfCancellationRequested();
                    }
                    catch (Exception) { result = "BLOCKED"; }
                    done.Add(command.CommandId, result);
                }
                // A lost acknowledgment is retried with the same command ID; never re-execute.
                await Post("/qa/v1/result", new QaResult(id, CandidateBuild.Sha, session, command.CommandId, result));
                if (result != "PASS") break;
            }
            await Task.Delay(1000, cancellationToken);
        }

        async Task<QaSnapshot> Post<T>(string path, T value)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, QaWire.Json)) };
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token);
                    if (!response.IsSuccessStatusCode || response.Headers.CacheControl?.NoStore != true) throw new InvalidDataException();
                    return JsonSerializer.Deserialize<QaSnapshot>(await CloudTransport.ReadBoundedAsync(response.Content, 8192, requestDeadline.Token), QaWire.Json)
                        ?? throw new InvalidDataException();
                }
                catch (HttpRequestException) when (attempt < 2) { await Task.Delay(1000, cancellationToken); }
            }
        }
    }
}
