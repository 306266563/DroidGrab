using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PayloadDumperPlus.App.Ui;

/// <summary>
/// 毛玻璃背景。Win10 上系统级 Acrylic（SetWindowCompositionAttribute）会把 WinForms 的子控件
/// 整个画没，所以这里自己来：截一张窗口所在位置的桌面 → 降采样再放大，得到近似高斯模糊。
/// </summary>
internal static class BlurBackground
{
    public static Bitmap? Capture(Rectangle bounds, int downscale = 10, float washAlpha = 0.28F)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        try
        {
            using var raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(raw))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

            return Blur(raw, downscale, washAlpha);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap Blur(Bitmap src, int downscale, float washAlpha)
    {
        int sw = Math.Max(2, src.Width / Math.Max(2, downscale));
        int sh = Math.Max(2, src.Height / Math.Max(2, downscale));

        using var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, sw, sh));
        }

        // 二次降采样，让噪点彻底糊掉
        int sw2 = Math.Max(2, sw / 2);
        int sh2 = Math.Max(2, sh / 2);
        using var smaller = new Bitmap(sw2, sh2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(smaller))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(small, new Rectangle(0, 0, sw2, sh2));
        }

        var result = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(smaller, new Rectangle(0, 0, src.Width, src.Height));

            // 压一层白纱，保证压在卡片上的文字始终读得清
            if (washAlpha > 0F)
            {
                int a = (int)Math.Clamp(255 * washAlpha, 0, 255);
                using var brush = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
                g.FillRectangle(brush, 0, 0, src.Width, src.Height);
            }
        }

        return result;
    }
}
