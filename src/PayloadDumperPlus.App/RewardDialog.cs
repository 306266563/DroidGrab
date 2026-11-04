using PayloadDumperPlus.App.Ui;

namespace PayloadDumperPlus.App;

/// <summary>
/// 赞赏页：上面是赞赏码，下面配一张“开源开发者续命咖啡”的梗图。
/// 两张图都是内嵌资源，绿色版单 exe 也能显示。
/// </summary>
public sealed class RewardDialog : Form
{
    private readonly Image? _reward = Assets.Image("reward");
    private readonly Image? _coffee = Assets.Image("coffee");

    public RewardDialog()
    {
        Text = "赞赏";
        ClientSize = new Size(430, 690);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Surface;
        Font = Theme.Ui(9F);
        DoubleBuffered = true;

        int y = 18;

        var title = new Label
        {
            Text = "写这个工具纯属用爱发电",
            Location = new Point(0, y),
            Width = ClientSize.Width,
            Height = 26,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Ui(11F, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
        };
        y += 30;

        var sub = new Label
        {
            Text = "如果它帮你省下了折腾的时间，可以请作者喝杯水",
            Location = new Point(0, y),
            Width = ClientSize.Width,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(150, 156, 168),
            BackColor = Color.Transparent,
        };
        y += 30;

        // 赞赏码：按比例缩放后居中
        PictureBox qr = MakePicture(_reward, 250, 250);
        qr.Location = new Point((ClientSize.Width - qr.Width) / 2, y);
        Controls.Add(qr);
        y += qr.Height + 14;

        var scanHint = new Label
        {
            Text = "微信 / 支付宝 扫一扫",
            Location = new Point(0, y),
            Width = ClientSize.Width,
            Height = 20,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(150, 156, 168),
            BackColor = Color.Transparent,
        };
        Controls.Add(scanHint);
        y += 34;

        // 梗图：更小一点，当点缀
        PictureBox meme = MakePicture(_coffee, 250, 204);
        meme.Location = new Point((ClientSize.Width - meme.Width) / 2, y);
        Controls.Add(meme);

        var close = new FlatButton
        {
            Text = "关闭",
            Primary = true,
            Width = 110,
            Height = 32,
            DialogResult = DialogResult.OK,
        };
        close.Location = new Point((ClientSize.Width - close.Width) / 2, ClientSize.Height - 50);
        Controls.Add(close);

        AcceptButton = close;
        CancelButton = close;

        Controls.Add(title);
        Controls.Add(sub);
    }

    /// <summary>等比缩放到不超过 max 尺寸，不留拉伸变形。</summary>
    private static PictureBox MakePicture(Image? source, int maxW, int maxH)
    {
        var box = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };

        if (source == null)
        {
            box.Size = new Size(maxW, 40);
            return box;
        }

        double scale = Math.Min(maxW / (double)source.Width, maxH / (double)source.Height);
        box.Size = new Size((int)(source.Width * scale), (int)(source.Height * scale));
        box.Image = source;
        return box;
    }
}
