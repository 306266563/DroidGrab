using System.Drawing;
using System.Drawing.Drawing2D;

namespace PayloadDumperPlus.App.Ui;

/// <summary>
/// 自绘圆角勾选框，用来替换系统 CheckBox。
/// 系统勾选框是方角 + 立体浮雕，跟贴片风对不上；这里画 22x22、圆角 7 的方块，
/// 未选中白底描边，选中填 Primary 并画白色对勾。
/// 全程不裁剪 Region：圆角抗锯齿交给 SmoothingMode.AntiAlias，而不是把圆角切出来（切出来就是硬锯齿）。
/// </summary>
internal sealed class FlatCheckBox : CheckBox
{
    // 22 / 7 / 10 是 96 DPI 下的设计值，实际按控件 DPI 换算，
    // 否则高 DPI 下勾选框和文字间距还是 22 物理像素，看着会缩水一圈。
    private int BoxSize => Theme.Scale(this, 22);
    private int BoxRadius => Theme.Scale(this, 7);
    private int TextGap => Theme.Scale(this, 10);

    private bool _hover;

    public FlatCheckBox()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.Selectable, true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.CardFill;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Ui(9F);
        Cursor = Cursors.Hand;
        AutoSize = true;
    }

    /// <summary>AutoSize 用：勾选框 + 间距 + 文字宽度。</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        Size textSize = string.IsNullOrEmpty(Text)
            ? Size.Empty
            : TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        int width = BoxSize + (textSize.Width > 0 ? TextGap + textSize.Width : 0) + Padding.Horizontal;
        int height = Math.Max(BoxSize, textSize.Height) + Padding.Vertical;
        return new Size(width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // 必须在这里清：OnPaintBackground 的清屏在这套 SetStyle 下不生效
        e.Graphics.Clear(Theme.CardFill);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int boxTop = Math.Max(0, (Height - BoxSize) / 2);
        // 盒子要小 1px：1px 描边以路径为中心，路径贴到控件右下边界时描边会整条落到控件之外
        // （GDI+ 里坐标 22 的 1px 线画在 22.5，正好出界）—— 未勾选的框看起来就是「底部缺口」。
        // 收成 0..21 之后四条边都落在控件里，而且都是整像素、不发糊。
        var boxRect = new Rectangle(0, boxTop, BoxSize - 1, BoxSize - 1);

        Color fill;
        Color border;
        if (!Enabled)
        {
            fill = Checked ? Color.FromArgb(190, 206, 228) : Color.FromArgb(242, 243, 246);
            border = Color.FromArgb(198, 204, 214);
        }
        else if (Checked)
        {
            fill = _hover ? Theme.PrimaryHover : Theme.Primary;
            border = fill;
        }
        else
        {
            fill = Color.White;
            border = _hover ? Theme.Primary : Theme.CardBorder;
        }

        using (GraphicsPath path = Drawing.Rounded(boxRect, BoxRadius))
        {
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            using var borderPen = new Pen(border, 1F);
            g.DrawPath(borderPen, path);
        }

        if (CheckState == CheckState.Indeterminate)
        {
            using var dashPen = new Pen(Enabled ? Theme.Primary : Color.FromArgb(170, 176, 188), 2F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            float midY = boxRect.Top + BoxSize / 2F;
            g.DrawLine(dashPen, boxRect.Left + 5.5F, midY, boxRect.Right - 5.5F, midY);
        }
        else if (Checked)
        {
            Color markColor = Enabled ? Theme.TextOnPrimary : Color.FromArgb(240, 244, 250);
            using var checkPen = new Pen(markColor, 2F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawLines(checkPen, new[]
            {
                new PointF(boxRect.Left + 5.5F, boxRect.Top + 11.5F),
                new PointF(boxRect.Left + 9.5F, boxRect.Top + 15F),
                new PointF(boxRect.Left + 16.5F, boxRect.Top + 7F),
            });
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var textRect = new Rectangle(BoxSize + TextGap, 0, Math.Max(1, Width - BoxSize - TextGap), Height);
            TextFormatFlags flags = TextFormatFlags.Left
                                    | TextFormatFlags.VerticalCenter
                                    | TextFormatFlags.NoPadding
                                    | TextFormatFlags.SingleLine
                                    | TextFormatFlags.EndEllipsis;
            if (!UseMnemonic)
            {
                flags |= TextFormatFlags.NoPrefix;
            }

            TextRenderer.DrawText(g, Text, Font, textRect,
                Enabled ? ForeColor : Color.FromArgb(170, 176, 188), flags);
        }

        if (Focused && ShowFocusCues && Width > 2 && Height > 2)
        {
            ControlPaint.DrawFocusRectangle(g, new Rectangle(0, 0, Width - 1, Height - 1));
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckedChanged(e);
    }

    protected override void OnCheckStateChanged(EventArgs e)
    {
        Invalidate();
        base.OnCheckStateChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (AutoSize)
        {
            Size = GetPreferredSize(Size.Empty);
        }

        // 尺寸变化后旧位置的像素落在当前矩形之外，父容器不重绘就永远擦不掉，看起来就是文字重影
        Parent?.Invalidate(true);
        Invalidate();

        Invalidate();
    }
}
