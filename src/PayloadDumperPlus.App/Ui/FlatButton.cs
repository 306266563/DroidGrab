using System.Drawing;
using System.Drawing.Drawing2D;

namespace PayloadDumperPlus.App.Ui;

/// <summary>
/// 自绘圆角按钮。系统按钮画不出大 R 角，只能自己来。
/// Primary=true 是实心强调色主按钮，否则是白底描边的次要按钮。
/// 圆角一律自绘：Region 是硬边裁剪，边缘会出现像素级锯齿。
/// </summary>
internal sealed class FlatButton : Button
{
    private bool _hover;
    private bool _pressed;

    public bool Primary { get; set; }
    public int Radius { get; set; } = Theme.ButtonRadius;

    /// <summary>
    /// 用三个自绘圆点代替文字（「更多 / 选择目录」这类小方块按钮）。
    /// 字符「…」在微软雅黑里本身偏下，TextRenderer 再怎么 VerticalCenter 也居不正，只能自己画。
    /// </summary>
    public bool DotsGlyph { get; set; }

    public FlatButton()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Ui(9F);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    /// <summary>
    /// 圆角外要露出底下的贴片卡片。只回绘直接父级是不够的：父级是透明的
    /// Panel / TableLayoutPanel 时它自己画不出任何东西，圆角外就会露出没被卡片
    /// 盖过的模糊底，看起来就是一圈灰色杂边。
    /// 所以从最顶层祖先开始逐层往下回绘：透明层自然什么都不画，卡片的圆角底就能透出来。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 直接铺不透明卡片底：这些控件都放在卡片里，卡片色本来就是它们该有的落底色。
        // 往上逐层回绘父背景在透明 Panel 链上会漏，控件移动后留下洗不掉的残影。
        e.Graphics.Clear(Theme.CardFill);
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
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // OnPaintBackground 里清屏在这套 SetStyle 下不生效，缓冲会留下上一帧内容，必须在这里清
        e.Graphics.Clear(Theme.CardFill);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = Drawing.Rounded(rect, Theme.Scale(this, Radius));

        Color fill, border, text;
        if (Primary)
        {
            fill = !Enabled ? Color.FromArgb(178, 200, 228)
                 : _pressed ? Theme.PrimaryPressed
                 : _hover ? Theme.PrimaryHover
                 : Theme.Primary;
            border = Color.Transparent;
            text = Enabled ? Theme.TextOnPrimary : Color.FromArgb(240, 244, 250);
        }
        else
        {
            fill = !Enabled ? Color.FromArgb(242, 243, 246)
                 : _pressed ? Color.FromArgb(226, 231, 238)
                 : _hover ? Theme.GhostHover
                 : Theme.GhostFill;
            border = _hover && Enabled ? Color.FromArgb(90, 52, 130, 255) : Theme.GhostBorder;
            text = Enabled ? Theme.TextPrimary : Color.FromArgb(170, 176, 188);
        }

        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        if (border.A > 0)
        {
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }

        if (DotsGlyph)
        {
            // 三个圆点，整体严格居中：直径 + 间距算总宽，从左边起摆
            const float dot = 3.6F;
            const float gap = 5.0F;
            float total = dot * 3 + gap * 2;
            float left = (Width - total) / 2F;
            float cy = Height / 2F;

            using var brush = new SolidBrush(text);
            for (int i = 0; i < 3; i++)
                g.FillEllipse(brush, left + i * (dot + gap), cy - dot / 2F, dot, dot);
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, rect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
