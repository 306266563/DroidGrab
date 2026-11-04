using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PayloadDumperPlus.App.Ui;

/// <summary>
/// 全局配色与尺寸。风格取小米「贴片」：大圆角、低饱和冷灰底、单一强调色。
/// 这里的尺寸常量都是 96 DPI 下的设计值，画到控件上时用 <see cref="Scale"/> 换算。
/// </summary>
internal static class Theme
{
    /// <summary>强调色（小米蓝）。</summary>
    public static readonly Color Primary = Color.FromArgb(52, 130, 255);
    public static readonly Color PrimaryHover = Color.FromArgb(30, 112, 242);
    public static readonly Color PrimaryPressed = Color.FromArgb(22, 96, 214);

    /// <summary>窗口底色（截图模糊失败时的兜底）。</summary>
    public static readonly Color Surface = Color.FromArgb(238, 240, 245);

    /// <summary>
    /// 卡片底色。刻意做成不透明：半透明卡片每重绘一次就只盖住一部分旧像素，
    /// 控件移动后会在原地留下洗不掉的残影。
    /// </summary>
    public static readonly Color CardFill = Color.FromArgb(255, 252, 253, 255);
    public static readonly Color CardBorder = Color.FromArgb(228, 231, 238);

    public static readonly Color LogText = Color.FromArgb(96, 104, 118);

    /// <summary>次要按钮（白底描边）。</summary>
    public static readonly Color GhostFill = Color.FromArgb(250, 255, 255, 255);
    public static readonly Color GhostHover = Color.FromArgb(255, 255, 255, 255);
    public static readonly Color GhostBorder = Color.FromArgb(46, 20, 30, 60);

    /// <summary>文本框内的实色填充（原生 TextBox 不支持半透明，只能实色）。</summary>
    public static readonly Color FieldFill = Color.FromArgb(240, 242, 247);

    public static readonly Color TextPrimary = Color.FromArgb(22, 24, 28);
    public static readonly Color TextSecondary = Color.FromArgb(122, 128, 140);
    public static readonly Color TextOnPrimary = Color.White;

    public static readonly Color RowAlt = Color.FromArgb(250, 251, 253);
    public static readonly Color RowSelected = Color.FromArgb(214, 230, 255);

    public const int CardRadius = 20;
    public const int ButtonRadius = 19;

    /// <summary>把 96 DPI 下的设计像素换算成控件当前 DPI 下的像素。</summary>
    public static int Scale(Control control, int designPixels)
        => (int)Math.Round(designPixels * (control.DeviceDpi / 96f), MidpointRounding.AwayFromZero);

    private static readonly string[] UiFontCandidates =
    {
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "SimSun",
    };

    private static readonly Lazy<string> UiFontFamily = new(ResolveUiFontFamily);

    /// <summary>挑第一个系统里装了的界面字体，都没有时退回 GDI+ 的通用无衬线字体。</summary>
    private static string ResolveUiFontFamily()
    {
        foreach (string name in UiFontCandidates)
        {
            try
            {
                using var family = new FontFamily(name);
                return name;
            }
            catch (ArgumentException)
            {
                // 这个字体没装，试下一个
            }
        }

        return FontFamily.GenericSansSerif.Name;
    }

    public static Font Ui(float size = 9F, FontStyle style = FontStyle.Regular)
        => new(UiFontFamily.Value, size, style);
}
