namespace ContinuityBridge.App;

internal sealed class SettingsForm : Form
{
    private readonly TextBox url = new() { Dock = DockStyle.Fill, PlaceholderText = "https://你的 Relay 地址" };
    private readonly TextBox secret = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "设备 token；留空保留此地址已有凭据" };
    private readonly CheckBox enable = new() { AutoSize = true, Text = "我了解以下说明，启用文本和图片自动同步" };
    private readonly CheckBox logon = new() { AutoSize = true, Text = "当前用户登录 Windows 时启动" };
    internal string BaseUrl => url.Text.Trim();
    internal string Token => secret.Text.Trim();
    internal bool AutoSync => enable.Checked;
    internal bool Logon => logon.Checked;

    internal SettingsForm(AppSettings settings)
    {
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ContinuityBridge 设置"; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 700); MinimumSize = new Size(800, 740); MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 8, AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Relay HTTPS 地址", AutoSize = true }); layout.Controls.Add(url);
        layout.Controls.Add(new Label { Text = "Windows 设备凭据（保存到 Windows 凭据管理器）", AutoSize = true }); layout.Controls.Add(secret);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(700, 0), Padding = new Padding(0, 12, 0, 12),
            Text = "启用后，新复制的文本和图片会自动上传到 Relay；远端未过期内容可能覆盖当前剪贴板。启动前已有内容不会自动上传。\n\n仅显式私密格式会被过滤，无法识别所有密码。v1 经 HTTPS 传输，服务器可读取内容；默认保留 30 分钟，服务重启会清空云端。过期不会清空本地。图片在本机还受解码内存限制。" });
        layout.Controls.Add(enable); layout.Controls.Add(logon);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                _ = Core.CloudTransport.ValidateOrigin(BaseUrl);
                if (Token.Length != 0 && (Token.Length != 64 || !Token.All(char.IsAsciiHexDigit))) throw new ArgumentException("invalid_token");
                DialogResult = DialogResult.OK;
            }
            catch (ArgumentException) { MessageBox.Show(this, "请填写有效的 HTTPS origin 和 64 位十六进制设备 token。", "检查设置"); }
        };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons); Controls.Add(layout);
        AcceptButton = save; CancelButton = cancel;
        url.Text = settings.BaseUrl; enable.Checked = settings.AutoSync; logon.Checked = settings.Logon;
    }
}
