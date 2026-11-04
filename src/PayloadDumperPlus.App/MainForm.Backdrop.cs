using System.Drawing.Drawing2D;
using PayloadDumperPlus.App.Ui;

namespace PayloadDumperPlus.App;

/// <summary>
/// 毛玻璃背景 + 贴片卡片的绘制。
/// 系统级 Acrylic 在 Win10 上会让 WinForms 子控件整个画不出来，所以改成自己截屏 + 自己模糊。
/// </summary>
public sealed partial class MainForm
{
    private Bitmap? _screenBlur;
    private readonly List<Control> _cards = new();
    private bool _capturingBackdrop;

    /// <summary>把一块区域登记成贴片卡片：它自己保持透明，由窗体统一画圆角底。</summary>
    private void RegisterCard(Control card)
    {
        card.BackColor = Color.Transparent;
        _cards.Add(card);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;

        if (_screenBlur != null)
        {
            Rectangle src = RectangleToScreen(ClientRectangle);
            var dst = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            Rectangle safe = Rectangle.Intersect(src, new Rectangle(0, 0, _screenBlur.Width, _screenBlur.Height));

            if (safe.Width > 0 && safe.Height > 0)
                g.DrawImage(_screenBlur, dst, safe, GraphicsUnit.Pixel);
            else
                g.Clear(Theme.Surface);
        }
        else
        {
            g.Clear(Theme.Surface);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cardRadius = Theme.Scale(this, Theme.CardRadius);
        foreach (Control card in _cards)
        {
            if (!card.Visible || card.Width <= 2 || card.Height <= 2 || card.Parent == null)
                continue;

            Point origin = PointToClient(card.Parent.PointToScreen(card.Location));
            var rect = new Rectangle(origin.X, origin.Y, card.Width - 1, card.Height - 1);
            using GraphicsPath path = Drawing.Rounded(rect, cardRadius);

            using (var brush = new SolidBrush(Theme.CardFill))
                g.FillPath(brush, path);
            using (var pen = new Pen(Theme.CardBorder))
                g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// 截一整屏并模糊，之后窗口怎么移动都从这张图里裁对应区域，
    /// 不重复截屏，免得拖动时一闪一闪。
    /// </summary>
    private void CaptureBackdrop()
    {
        if (_capturingBackdrop || _screenBlur != null)
            return;

        _capturingBackdrop = true;

        try
        {
            // 必须把窗口真正从屏幕上撤掉。只设 Opacity = 0 时 DWM 还会留一帧残像，
            // 那一帧会被一起模糊进背景图，之后任何重绘都擦不掉，满屏全是鬼影。
            Hide();
            Application.DoEvents();
            Thread.Sleep(260);

            Rectangle screen = SystemInformation.VirtualScreen;
            _screenBlur = BlurBackground.Capture(screen, 12, 0.5F);
        }
        catch
        {
            _screenBlur = null;
        }
        finally
        {
            Show();
            _capturingBackdrop = false;
            Invalidate(true);
        }
    }
}
