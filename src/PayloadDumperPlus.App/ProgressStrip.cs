using System.Drawing.Drawing2D;

namespace PayloadDumperPlus.App;

/// <summary>
/// 自绘进度条：细、全圆角、单色填充。
/// Value / Stalled / Running 变化时各自触发一次重绘；Stalled 为真时填充色转淡。
/// </summary>
public sealed class ProgressStrip : Control
{
    private bool _running;
    private double _value;
    private bool _stalled;

    /// <summary>条本身的高度（控件更高时上下居中绘制）。</summary>
    public int TrackHeight { get; set; } = 8;

    public Color AccentColor { get; set; } = Color.FromArgb(52, 130, 255);
    public Color TrackColor { get; set; } = Color.FromArgb(232, 234, 240);

    /// <summary>0..1。</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;

            _value = value;
            Invalidate();
        }
    }

    /// <summary>true 表示一段时间没有真实进度，填充色转淡以示区别。</summary>
    public bool Stalled
    {
        get => _stalled;
        set
        {
            if (_stalled == value)
                return;

            _stalled = value;
            Invalidate();
        }
    }

    public bool Running
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            Invalidate();
        }
    }

    public ProgressStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Height = TrackHeight;
    }

    /// <summary>整条在 OnPaint 里自己画，这里不刷系统底色。</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int h = Math.Min(TrackHeight, Height);
        if (h < 4) h = Math.Max(4, Height);
        int top = (Height - h) / 2;
        int radius = h / 2;

        var trackRect = new Rectangle(0, top, Width - 1, h - 1);
        using (GraphicsPath track = Rounded(trackRect, radius))
        using (var trackBrush = new SolidBrush(TrackColor))
            g.FillPath(trackBrush, track);

        double value = Math.Clamp(Value, 0, 1);
        if (value <= 0)
            return;

        int width = (int)Math.Round((Width - 1) * value);
        if (width < h) width = h;                 // 刚起步时至少画成一个圆点
        if (width > Width - 1) width = Width - 1;

        var doneRect = new Rectangle(0, top, width, h - 1);
        Color fill = Stalled ? Blend(AccentColor, Color.White, 0.45F) : AccentColor;

        using GraphicsPath done = Rounded(doneRect, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, done);
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0F, 1F);
        return Color.FromArgb(
            from.A,
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    private static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;

        if (rectangle.Width <= d || rectangle.Height <= d)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        path.AddArc(rectangle.X, rectangle.Y, d, d, 180, 90);
        path.AddArc(rectangle.Right - d, rectangle.Y, d, d, 270, 90);
        path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
