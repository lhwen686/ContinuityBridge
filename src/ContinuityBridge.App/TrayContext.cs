using ContinuityBridge.Core;
using ContinuityBridge.Windows;

namespace ContinuityBridge.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem status = new("已暂停") { Enabled = false };
    private readonly ToolStripMenuItem activity = new("尚无同步操作") { Enabled = false };
    private readonly ToolStripMenuItem toggle = new("启用同步");
    private readonly ToolStripMenuItem clear = new("清空云端最新项") { Enabled = false };
    private AppSettings settings = AppSettings.Load();
    private CloudClipboardAdapter? clipboard;
    private CloudTransport? transport;
    private CloudSyncEngine? engine;
    private CancellationTokenSource? lifetime;
    private Task? run;
    private bool busy;
    private bool exiting;

    internal TrayContext()
    {
        _ = dispatcher.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.Add(status);
        menu.Items.Add(activity);
        menu.Items.Add("设置…", null, async (_, _) => await ConfigureAsync());
        toggle.Click += async (_, _) => await ToggleAsync();
        clear.Click += (_, _) => engine?.RequestClear();
        menu.Items.Add(toggle); menu.Items.Add(clear); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await QuitAsync());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "ContinuityBridge · 已暂停", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += async (_, _) => await ConfigureAsync();
        dispatcher.BeginInvoke((Action)(async () =>
        {
            if (settings.AutoSync) await StartAsync();
            else if (settings.BaseUrl.Length == 0) await ConfigureAsync();
        }));
    }

    private void SetStatus(SyncStatus value)
    {
        if (dispatcher.IsDisposed) return;
        dispatcher.BeginInvoke((Action)(() =>
        {
            string description = value switch
            {
                SyncStatus.Connecting => "正在连接…", SyncStatus.Connected => "已连接 · 自动同步中",
                SyncStatus.Polling => "已连接 · HTTP 轮询", SyncStatus.LocalSent => "本地复制已发送",
                SyncStatus.RemoteApplied => "远端内容已写入剪贴板", SyncStatus.Unsupported => "本次复制未发送：私密或不支持的格式",
                SyncStatus.Offline => "网络不可用 · 等待重连", SyncStatus.OfflineDiscarded => "旧复制未补传，请重新复制",
                SyncStatus.Conflict => "云端已有更新，本次操作未覆盖；请重试", SyncStatus.AuthenticationRequired => "凭据无效或已撤销，请打开设置",
                SyncStatus.InvalidContent => "内容或服务不兼容，请检查限额与候选版本", SyncStatus.CloudCleared => "云端已清空，本地剪贴板保留",
                _ => "已暂停"
            };
            if (value is SyncStatus.Connecting or SyncStatus.Connected or SyncStatus.Polling or SyncStatus.Offline or SyncStatus.AuthenticationRequired or SyncStatus.Paused)
                status.Text = description;
            else activity.Text = description;
            string label = "ContinuityBridge · " + status.Text;
            tray.Text = label[..Math.Min(63, label.Length)];
        }));
    }

    private Task StartAsync()
    {
        try
        {
            var token = DeviceCredentialStore.Read(settings.BaseUrl);
            if (token is null) { SetStatus(SyncStatus.AuthenticationRequired); return Task.CompletedTask; }
            transport = new(settings.BaseUrl, token);
            clipboard = new(_ => SetStatus(SyncStatus.InvalidContent));
            engine = new(clipboard, transport); engine.StatusChanged += SetStatus;
            lifetime = new();
            var activeEngine = engine; var cancellation = lifetime.Token;
            run = Task.Run(() => activeEngine.RunAsync(cancellation));
            _ = ObserveRunAsync(run);
            toggle.Text = "暂停同步"; clear.Enabled = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { SetStatus(SyncStatus.AuthenticationRequired); }
        return Task.CompletedTask;
    }

    private async Task ObserveRunAsync(Task task)
    {
        try { await task; }
        catch (Exception) { SetStatus(SyncStatus.InvalidContent); }
    }

    private async Task StopAsync()
    {
        if (lifetime is not null) await lifetime.CancelAsync();
        if (run is not null) try { await run; } catch (Exception) { SetStatus(SyncStatus.InvalidContent); }
        if (clipboard is not null) await clipboard.DisposeAsync();
        transport?.Dispose(); lifetime?.Dispose();
        lifetime = null; run = null; clipboard = null; transport = null; engine = null;
        toggle.Text = "启用同步"; clear.Enabled = false;
        SetStatus(SyncStatus.Paused);
    }

    private async Task ToggleAsync()
    {
        if (busy) return;
        if (lifetime is null) { await ConfigureAsync(); return; }
        busy = true;
        try { await StopAsync(); settings = settings with { AutoSync = false }; settings.Save(); }
        catch (IOException) { SetStatus(SyncStatus.InvalidContent); }
        finally { busy = false; }
    }

    private async Task ConfigureAsync()
    {
        if (busy || exiting) return;
        busy = true;
        try
        {
            using var form = new SettingsForm(settings);
            if (form.ShowDialog() != DialogResult.OK) return;
            await StopAsync();
            string origin = CloudTransport.ValidateOrigin(form.BaseUrl).AbsoluteUri.TrimEnd('/');
            if (form.Token.Length > 0) DeviceCredentialStore.Save(origin, form.Token);
            else if (DeviceCredentialStore.Read(origin) is null) throw new IOException("credential_required");
            if (form.Logon || settings.Logon != form.Logon) AppSettings.SetLogon(form.Logon);
            settings = new(origin, form.AutoSync, form.Logon); settings.Save();
            if (settings.AutoSync) await StartAsync();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { MessageBox.Show("设置未能保存。请检查 HTTPS 地址、设备凭据和当前用户存储权限。", "ContinuityBridge"); }
        finally { busy = false; }
    }

    private async Task QuitAsync()
    {
        if (busy || exiting) return;
        exiting = true;
        await StopAsync(); tray.Visible = false; ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tray.Dispose(); dispatcher.Dispose(); }
        base.Dispose(disposing);
    }
}
