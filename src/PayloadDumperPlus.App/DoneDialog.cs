namespace PayloadDumperPlus.App;

/// <summary>提取结束后的小提示窗：一行结论 + 一行耗时，底下确定。</summary>
public sealed class DoneDialog : Form
{
    public DoneDialog(string headline, string detail)
    {
        Text = "完成";
        ClientSize = new Size(360, 168);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        var icon = new Label
        {
            Text = "✓",
            Font = new Font("Segoe UI", 26F, FontStyle.Bold),
            ForeColor = Color.FromArgb(34, 170, 92),
            Location = new Point(24, 26),
            AutoSize = true,
        };

        var title = new Label
        {
            Text = headline,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            Location = new Point(78, 30),
            AutoSize = true,
            ForeColor = Color.FromArgb(32, 36, 44),
        };

        var subtitle = new Label
        {
            Text = detail,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(80, 64),
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 118, 130),
        };

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Size = new Size(96, 32),
            Location = new Point(ClientSize.Width - 120, ClientSize.Height - 48),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            FlatStyle = FlatStyle.System,
        };

        Controls.Add(icon);
        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = ok;
        ActiveControl = ok;

        // 打开即把焦点放在确定上：直接回车就能关，不用先 Tab 过去
        Shown += (_, _) => ok.Focus();
    }
}
