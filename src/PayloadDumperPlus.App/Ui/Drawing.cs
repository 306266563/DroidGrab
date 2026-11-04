using System.Drawing;
using System.Drawing.Drawing2D;

namespace PayloadDumperPlus.App.Ui;

internal static class Drawing
{
    /// <summary>
    /// 生成圆角矩形路径。半径会自动收敛到不超过矩形短边的一半；
    /// 传入的半径由调用方按控件 DPI 换算，这里只负责画。
    /// </summary>
    public static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        d = Math.Min(d, Math.Min(rect.Width, rect.Height));
        if (d < 2)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
