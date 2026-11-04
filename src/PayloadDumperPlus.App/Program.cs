namespace PayloadDumperPlus.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);

        AppStartupOptions options = ParseArguments(args);

        Application.Run(new MainForm(options.Source, options.AutoStart, options.OutputDirectory, options.ForcedParts));
    }

    /// <summary>命令行参数：DroidGrab [包路径或直链] [--auto] [--out 目录] [--parts a,b]。</summary>
    private sealed record AppStartupOptions(
        string? Source,
        bool AutoStart,
        string? OutputDirectory,
        IReadOnlyCollection<string>? ForcedParts);

    private static AppStartupOptions ParseArguments(string[] args)
    {
        string? source = null;
        bool autoStart = false;
        string? outputDirectory = null;
        IReadOnlyCollection<string>? forcedParts = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? inlineValue = null;

            // "--out=目录" 这种写法先拆成键和值；位置参数（可能是个带 = 的路径）不动。
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                int equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    inlineValue = arg[(equals + 1)..];
                    arg = arg[..equals];
                }
            }

            switch (arg.ToLowerInvariant())
            {
                case "--auto":
                    autoStart = true;
                    break;

                case "--out":
                    outputDirectory = inlineValue ?? NextValue(args, ref i, "--out");
                    break;

                case "--parts":
                    string? parts = inlineValue ?? NextValue(args, ref i, "--parts");
                    if (!string.IsNullOrWhiteSpace(parts))
                    {
                        forcedParts = parts
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToArray();
                    }
                    break;

                default:
                    // 位置参数（包路径或直链）只认第一个；--out / --parts 的值在上面已经取走，
                    // 不会被当成输入源。
                    if (!arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        source ??= arg;
                        break;
                    }

                    // 不认识的 -- 参数直接报错退出：否则它后面的值会被当成输入源，
                    // 用户看到的是"找不到文件 32"这种莫名其妙的错误。
                    MessageBox.Show(
                        $"不认识的参数：{arg}{Environment.NewLine}{Environment.NewLine}"
                        + "支持的参数：" + Environment.NewLine
                        + "  <输入>          包路径或直链" + Environment.NewLine
                        + "  --auto          启动后立即开始提取" + Environment.NewLine
                        + "  --out <目录>    输出目录" + Environment.NewLine
                        + "  --parts a,b     只提取这些分区",
                        "参数不正确", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Environment.Exit(2);
                    break;
            }
        }

        return new AppStartupOptions(source, autoStart, outputDirectory, forcedParts);
    }

    private static string? NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            MessageBox.Show($"{option} 后面要跟一个值，这次先忽略它。", "参数不完整",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        index++;
        return args[index];
    }
}
