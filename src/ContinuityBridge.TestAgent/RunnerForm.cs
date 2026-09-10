using ContinuityBridge.Core;
using ContinuityBridge.Qa.Protocol;
using ContinuityBridge.Windows;

namespace ContinuityBridge.TestAgent;

internal sealed class RunnerForm : Form
{
    private readonly TextBox origin = new() { Width = 700, AccessibleName = "QA HTTPS 地址", PlaceholderText = "QA staging HTTPS origin" };
    private readonly TextBox runId = new() { Width = 700, AccessibleName = "测试 runId", PlaceholderText = "短期 runId" };
    private readonly TextBox token = new() { Width = 700, AccessibleName = "Runner 凭据", UseSystemPasswordChar = true, PlaceholderText = "runner token，仅内存" };
    private readonly CheckBox stagingSync = new() { AutoSize = true, Text = "启用本次隔离的 staging 同步（P6 双向场景需要）" };
    private readonly TextBox relayOrigin = new() { Width = 700, AccessibleName = "测试 Relay HTTPS 地址", PlaceholderText = "独立 staging Relay HTTPS origin；不读取产品设置" };
    private readonly TextBox relayToken = new() { Width = 700, AccessibleName = "测试 Relay 凭据", UseSystemPasswordChar = true, PlaceholderText = "staging Windows device token，仅内存" };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(700, 0), Text = "未启用；没有读取剪贴板或连接 QA。" };
    private readonly CheckBox consent = new() { AutoSize = true, Text = "已保管重要内容，授权本次测试窗口；发现外部复制立即停止" };
    private readonly Button start = new() { Text = "开始测试", AutoSize = true };
    private readonly Button stop = new() { Text = "立即停止", AutoSize = true, Enabled = false };
    private CancellationTokenSource? lifetime;
    private bool running;
    private bool closeRequested;
    private bool poisoned;
    private readonly TaskCompletionSource closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // If a native call has not returned, keep the interlock until process exit.
    private static readonly List<Mutex> QuarantinedInterlocks = [];

    internal RunnerForm()
    {
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ContinuityBridge TestAgent · 合成测试与内存恢复"; ClientSize = new Size(780, 690); MinimumSize = new Size(800, 710);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        layout.Controls.Add(new Label { AutoSize = true, Text = "候选 SHA：" + CandidateBuild.Sha });
        layout.Controls.Add(origin); layout.Controls.Add(runId); layout.Controls.Add(token);
        layout.Controls.Add(stagingSync); layout.Controls.Add(relayOrigin); layout.Controls.Add(relayToken);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(700, 0), Text = "先验证 QA 身份/角色/租约，再备份支持格式到本进程内存，最后写合成 fixture。未知格式、超限、竞争均停止且不覆盖。请先手动退出日常产品 App。清理先停止全部测试任务，仅无外部复制时恢复；强杀/断电无法恢复。" });
        layout.Controls.Add(consent); layout.Controls.Add(start); layout.Controls.Add(stop); layout.Controls.Add(status); Controls.Add(layout);
        start.Click += async (_, _) => await StartRunAsync();
        stop.Click += (_, _) => lifetime?.Cancel();
        FormClosing += (_, e) =>
        {
            if (running)
            { e.Cancel = true; closeRequested = true; closing.TrySetResult(); lifetime?.Cancel(); status.Text = "正在停止任务并进行有界清理…"; }
        };
    }

    private async Task StartRunAsync()
    {
        if (running || poisoned || !consent.Checked) return;
        Mutex? productIsolation = null;
        bool ownsIsolation = false;
        RunnerSession? session = null;
        running = true;
        using var cancellation = new CancellationTokenSource();
        lifetime = cancellation;
        try
        {
            Uri uri = CloudTransport.ValidateOrigin(origin.Text.Trim());
            string id = runId.Text.Trim(), secret = token.Text.Trim();
            if (!QaWire.IsId(id) || !QaWire.IsHash(secret) || !QaWire.IsSha(CandidateBuild.Sha)) throw new InvalidDataException();
            string? syncOrigin = null, syncToken = null;
            if (stagingSync.Checked)
            {
                syncOrigin = CloudTransport.ValidateOrigin(relayOrigin.Text.Trim()).AbsoluteUri;
                syncToken = relayToken.Text.Trim();
                if (!QaWire.IsHash(syncToken)) throw new InvalidDataException();
            }
            // Product Program uses this exact singleton for its WHOLE process lifetime.
            // Holding it also blocks product logon startup during capture/run/restore.
            productIsolation = new Mutex(true, @"Local\ContinuityBridge.Product.v1", out ownsIsolation);
            if (!ownsIsolation)
            { status.Text = "未接管：日常产品 App 仍在运行。请手动退出后重试；没有读取剪贴板或连接 QA。"; return; }
            foreach (Control control in new Control[] { start, origin, runId, token, stagingSync, relayOrigin, relayToken, consent }) control.Enabled = false;
            stop.Enabled = true; token.Clear(); relayToken.Clear();
            // stop can be raised on the clipboard STA or product worker; CTS is thread safe.
            var desktop = new FixtureDesktop(() => cancellation.Cancel());
            session = new(new RunnerQa(uri, secret), desktop, new RunnerSync(desktop, syncOrigin, syncToken, () => cancellation.Cancel()),
                id, CandidateBuild.Sha, Guid.NewGuid().ToString(), text => status.Text = text);
            RunnerOutcome outcome = await session.RunAsync(true, cancellation.Token);
            string restore = outcome.Restore switch
            {
                RestoreOutcome.Restored => "已恢复支持格式的内存备份，并标记禁止自动上传。",
                RestoreOutcome.Untouched => "未覆盖剪贴板。若快照不支持，请自行保管内容并准备纯文本/图片后重试。",
                RestoreOutcome.SkippedExternalCopy => "检测到新的外部复制；已保留新内容并跳过恢复。",
                RestoreOutcome.TimedOut => "清理超时；不宣称已恢复。写入已取消，重新启用被禁用。",
                _ => "恢复失败；不宣称已恢复，请使用自行保管的内容。",
            };
            status.Text = outcome.Reason + "；" + restore;
            if (outcome.CleanupTimedOut)
            {
                // Keep the product interlock until pending native work returns.
                // UI stays responsive; a requested close may end the process with no recovery promise.
                poisoned = true; stop.Enabled = false;
                if (!closeRequested) await Task.WhenAny(session.Quiesced, closing.Task);
            }
        }
        catch (Exception) { status.Text = "未能完成测试：配置或清理异常。未记录或回传剪贴板正文；请检查本机状态。"; }
        finally
        {
            lifetime = null; running = false;
            if (ownsIsolation && session?.Quiesced.IsCompleted == false)
                QuarantinedInterlocks.Add(productIsolation!);
            else
            {
                if (ownsIsolation) productIsolation?.ReleaseMutex();
                productIsolation?.Dispose();
            }
            foreach (Control control in new Control[] { start, origin, runId, token, stagingSync, relayOrigin, relayToken, consent }) control.Enabled = true;
            start.Enabled = !poisoned; stop.Enabled = false; consent.Checked = false;
            if (closeRequested) Close();
        }
    }
}
