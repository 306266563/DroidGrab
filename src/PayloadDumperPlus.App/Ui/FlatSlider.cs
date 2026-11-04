using System.Drawing;
using System.Drawing.Drawing2D;

namespace PayloadDumperPlus.App.Ui;

/// <summary>
/// 自绘滑杆，用来替换系统 TrackBar。
/// 系统轨道 + 方形滑块的观感太旧，这里用 8px 圆角轨道 + 20px 圆形滑块，
/// 已完成部分填 Primary，未完成部分填 FieldFill。
/// 全程不裁剪 Region：圆角与圆形都靠 SmoothingMode.AntiAlias 抗锯齿。
/// </summary>
internal sealed class FlatSlider : Control
{
    // 8 / 4 / 20 / 40 是 96 DPI 下的设计值，画的时候按控件 DPI 换算。
    private int TrackHeight => Theme.Scale(this, 8);
    private int TrackRadius => Theme.Scale(this, 4);
    private int ThumbSize => Theme.Scale(this, 20);
    private int PreferredHeight => Theme.Scale(this, 40);

    private int _minimum;
    private int _maximum = 10;
    private int _value;
    private int _smallChange = 1;
    private int _largeChange = 5;
    private bool _dragging;
    private bool _hover;

    /// <summary>值变化时触发，签名与 TrackBar.ValueChanged 一致。</summary>
    public event EventHandler? ValueChanged;

    public FlatSlider()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.Selectable, true);

        BackColor = Color.Transparent;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Ui(9F);
        Size = new Size(240, PreferredHeight);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public int Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value)
            {
                return;
            }

            _minimum = value;
            if (_maximum < _minimum)
            {
                _maximum = _minimum;
            }

            bool coerced = CoerceValue();
            Invalidate();
            if (coerced)
            {
                RaiseValueChanged();
            }
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum == value)
            {
                return;
            }

            _maximum = value;
            if (_minimum > _maximum)
            {
                _minimum = _maximum;
            }

            bool coerced = CoerceValue();
            Invalidate();
            if (coerced)
            {
                RaiseValueChanged();
            }
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            int coerced = Math.Clamp(value, _minimum, _maximum);
            if (coerced == _value)
            {
                return;
            }

            _value = coerced;
            Invalidate();
            RaiseValueChanged();
        }
    }

    public int SmallChange
    {
        get => _smallChange;
        set => _smallChange = Math.Max(1, value);
    }

    public int LargeChange
    {
        get => _largeChange;
        set => _largeChange = Math.Max(1, value);
    }

    /// <summary>AutoSize 打开时只固定一个舒服的高度，宽度仍由调用方或父容器决定。</summary>
    public override bool AutoSize
    {
        get => base.AutoSize;
        set
        {
            base.AutoSize = value;
            if (value)
            {
                Height = PreferredHeight;
            }
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
        => new(proposedSize.Width > 0 ? proposedSize.Width : Width, PreferredHeight);

    /// <summary>
    /// 透明控件的背景靠自己往上找祖先逐层回绘：父级若是透明的 Panel，
    /// 它自己画不出东西，圆角外就会露出上一帧的残影。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 直接铺不透明卡片底：这些控件都放在卡片里，卡片色本来就是它们该有的落底色。
        // 往上逐层回绘父背景在透明 Panel 链上会漏，控件移动后留下洗不掉的残影。
        e.Graphics.Clear(Theme.CardFill);
    }


    protected override void OnPaint(PaintEventArgs e)
    {
        // OnPaintBackground 里清屏在这套 SetStyle 下不生效，缓冲会留下上一帧内容，必须在这里清
        e.Graphics.Clear(Theme.CardFill);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        GetMetrics(out int left, out int right, out int trackTop);
        int trackWidth = Math.Max(1, right - left);
        var trackRect = new Rectangle(left, trackTop, trackWidth, TrackHeight);

        using (GraphicsPath trackPath = Drawing.Rounded(trackRect, TrackRadius))
        using (var trackBrush = new SolidBrush(Enabled ? Theme.FieldFill : Color.FromArgb(236, 238, 242)))
        {
            g.FillPath(trackBrush, trackPath);
        }

        float ratio = Ratio;
        float centerY = trackTop + TrackHeight / 2F;
        float thumbCenterX = left + trackWidth * ratio;

        int doneWidth = (int)Math.Round(thumbCenterX) - left;
        if (doneWidth > 0)
        {
            var doneRect = new Rectangle(left, trackTop, doneWidth, TrackHeight);
            using GraphicsPath donePath = Drawing.Rounded(doneRect, TrackRadius);
            using var doneBrush = new SolidBrush(DoneColor);
            g.FillPath(doneBrush, donePath);
        }

        var thumbRect = new RectangleF(thumbCenterX - ThumbSize / 2F, centerY - ThumbSize / 2F, ThumbSize, ThumbSize);

        if (Enabled)
        {
            using var shadow = new SolidBrush(Color.FromArgb(26, 16, 30, 60));
            g.FillEllipse(shadow,
                thumbRect.X, thumbRect.Y + 1F, thumbRect.Width, thumbRect.Height);
        }

        using (var thumbBrush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(246, 247, 250)))
        {
            g.FillEllipse(thumbBrush, thumbRect);
        }

        Color thumbBorder = !Enabled ? Color.FromArgb(202, 208, 218)
                          : _dragging ? Theme.PrimaryPressed
                          : Theme.Primary;
        using (var thumbPen = new Pen(thumbBorder, 1.4F))
        {
            g.DrawEllipse(thumbPen, thumbRect);
        }

        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Color.FromArgb(64, Theme.Primary), 2F);
            g.DrawEllipse(ring, RectangleF.Inflate(thumbRect, 4F, 4F));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();
        _dragging = true;
        Capture = true;
        SetValueFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetValueFromX(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = false;
        Capture = false;
        Invalidate();
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

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.Home:
            case Keys.End:
            case Keys.PageUp:
            case Keys.PageDown:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!Enabled)
        {
            base.OnKeyDown(e);
            return;
        }

        bool handled = true;
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                Value -= _smallChange;
                break;
            case Keys.Right:
            case Keys.Up:
                Value += _smallChange;
                break;
            case Keys.PageDown:
                Value -= _largeChange;
                break;
            case Keys.PageUp:
                Value += _largeChange;
                break;
            case Keys.Home:
                Value = Minimum;
                break;
            case Keys.End:
                Value = Maximum;
                break;
            default:
                handled = false;
                break;
        }

        e.Handled = handled;
        e.SuppressKeyPress = handled;
        base.OnKeyDown(e);
    }

    /// <summary>轨道左右端各留出半个滑块的宽度，保证圆形滑块完整落在控件矩形内。</summary>
    private void GetMetrics(out int left, out int right, out int trackTop)
    {
        int inset = ThumbSize / 2;
        left = inset;
        right = Math.Max(left + 1, Width - inset - 1);
        trackTop = Math.Max(0, (Height - TrackHeight) / 2);
    }

    private float Ratio
        => _maximum > _minimum
            ? Math.Clamp((_value - _minimum) / (float)(_maximum - _minimum), 0F, 1F)
            : 0F;

    private Color DoneColor
        => !Enabled ? Color.FromArgb(180, 196, 220)
         : _dragging ? Theme.PrimaryPressed
         : _hover ? Theme.PrimaryHover
         : Theme.Primary;

    private bool CoerceValue()
    {
        int coerced = Math.Clamp(_value, _minimum, _maximum);
        if (coerced == _value)
        {
            return false;
        }

        _value = coerced;
        return true;
    }

    private void RaiseValueChanged() => ValueChanged?.Invoke(this, EventArgs.Empty);

    private void SetValueFromX(int x)
    {
        GetMetrics(out int left, out int right, out _);
        int span = right - left;
        if (span <= 0)
        {
            return;
        }

        float ratio = Math.Clamp((x - left) / (float)span, 0F, 1F);
        Value = _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
    }
}
