using PayloadDumperPlus.App.Ui;

namespace PayloadDumperPlus.App;

/// <summary>设置：目录与请求头。日常很少改的东西都收在这里，主界面只留输出目录。</summary>
public sealed class SettingsDialog : Form
{
    private readonly TextBox _ua = new();
    private readonly TextBox _referer = new();
    private readonly TextBox _cookie = new();

    public AppSettings Result { get; }

    public SettingsDialog(AppSettings current)
    {
        Result = current;

        Text = "设置";
        ClientSize = new Size(660, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Surface;
        Font = Theme.Ui(9F);
        ShowInTaskbar = false;

        int y = 14;

        AddRow("User-Agent", _ua, current.UserAgent, ref y, "留空 = 内置 Chrome UA");
        AddRow("Referer", _referer, current.Referer, ref y, "防盗链需要时填");
        AddRow("Cookie", _cookie, current.Cookie, ref y, "不写日志；需要登录的链接才用");

        var autoDown = new FlatCheckBox
        {
            Text = "自动降档",
            Location = new Point(16, y),
            AutoSize = true,
            Checked = current.AutoDownshift,
        };
        var autoHint = new Label
        {
            Text = "开启后检测到多线程速度慢或卡顿，则自动降档到更少线程，以达到提升速度的效果",
            Location = new Point(34, y + 24),
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 156, 168),
        };
        var autoHelp = new FlatButton
        {
            Text = "?",
            Location = new Point(96, y - 3),
            Size = new Size(22, 22),
            Font = Theme.Ui(9F, FontStyle.Bold),
        };
        autoHelp.Click += (_, _) => MessageBox.Show(this,
            string.Join(Environment.NewLine, new[]
            {
                "自动降档",
                string.Empty,
                "· 每 4 秒采样一次实际吞吐。",
                "· 吞吐比上一窗口下滑超过 30% 时，并发数减半，最低降到 1。",
                "· 吞吐回升超过 30% 时，并发数逐步加回，但不超过线程滑块设定的值。",
                string.Empty,
                "如果运行中发现吞吐量下滑，程序会逐档减少并发；等到吞吐量回升后，再慢慢加回来；上限仍为滑块设定的值。手动拖动滑块时，这个上限会立即改变。",
                string.Empty,
                "为什么需要它：部分源（例如按 IP 限速的 CDN）连接开得越多，每条连接分到的带宽越少，总速度反而下降。自动降档会在这种情况下把并发收回来。",
            }),
            "自动降档说明", MessageBoxButtons.OK, MessageBoxIcon.Information);

        Controls.Add(autoDown);
        Controls.Add(autoHint);
        Controls.Add(autoHelp);

        var ok = new FlatButton { Text = "保存", DialogResult = DialogResult.OK, Width = 100, Height = 30, Primary = true };
        var cancel = new FlatButton { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80, Height = 30 };
        ok.Location = new Point(ClientSize.Width - 200, ClientSize.Height - 48);
        cancel.Location = new Point(ClientSize.Width - 108, ClientSize.Height - 48);
        ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            Result.UserAgent = NullIfEmpty(_ua.Text);
            Result.Referer = NullIfEmpty(_referer.Text);
            Result.Cookie = NullIfEmpty(_cookie.Text);
            Result.AutoDownshift = autoDown.Checked;
        };
    }

    private void AddRow(string label, TextBox box, string? value, ref int y, string hint)
    {
        var caption = new Label
        {
            Text = label,
            Location = new Point(16, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 98, 112),
        };

        box.Location = new Point(110, y - 2);
        box.Width = 420;
        box.Font = new Font("Consolas", 9F);
        box.Text = value ?? string.Empty;

        var tip = new Label
        {
            Text = hint,
            Location = new Point(110, y + 22),
            AutoSize = true,
            ForeColor = Color.FromArgb(150, 156, 168),
        };

        Controls.Add(caption);
        Controls.Add(box);
        Controls.Add(tip);

        y += 58;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
