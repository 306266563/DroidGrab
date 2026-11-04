using System.Reflection;

namespace PayloadDumperPlus.App.Ui;

/// <summary>
/// 内嵌图片资源。单文件发布时不能读磁盘路径，只能走嵌入资源。
/// </summary>
internal static class Assets
{
    /// <summary>按短名取图，取不到返回 null（调用方自己决定怎么兜底）。</summary>
    public static Image? Image(string name)
    {
        try
        {
            Assembly asm = typeof(Assets).Assembly;
            using Stream? stream = asm.GetManifestResourceStream($"DroidGrab.Assets.{name}.png");
            if (stream == null)
                return null;

            // GDI+ 只在解码期间要求流可用，所以这里立刻复制成独立 Bitmap，
            // 调用方拿到的图像不再依赖资源流，流随 using 正常释放。
            using var decoded = System.Drawing.Image.FromStream(stream);
            return new Bitmap(decoded);
        }
        catch
        {
            return null;
        }
    }
}
