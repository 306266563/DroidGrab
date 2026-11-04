using System.Text.Json;

namespace PayloadDumperPlus.App;

/// <summary>绿色版配置：优先放 exe 同目录，不可写时退到 %LOCALAPPDATA%。</summary>
public sealed class AppSettings
{
    public string? OutputDirectory { get; set; }
    public string? UserAgent { get; set; }
    public string? Referer { get; set; }
    public string? Cookie { get; set; }
    // 默认给到 16：CDN 常按「单连接」限速（实测小米约 600 KB/s 一条），
    // 连接开少了总速就被卡在这里，用户还得自己猜该拖到多少。
    public int DownloadConnections { get; set; } = 16;
    public int ExtractThreads { get; set; }
    public bool AutoDownshift { get; set; } = true;

    /// <summary>最近一次 Save 成功的文件路径；从来没写成功过就是 null。</summary>
    public static string? LastWritePath { get; private set; }

    private static string PrimaryPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DroidGrab", "settings.json");

    /// <summary>0.6.0 之前的目录名，老配置还能读出来，新的写到 DroidGrab 下。</summary>
    private static string LegacyFallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PayloadDumperPlus", "settings.json");

    public static AppSettings Load()
    {
        foreach (string path in new[] { PrimaryPath, FallbackPath, LegacyFallbackPath })
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
            catch
            {
                // 配置损坏就走默认值，别拦着程序启动
            }
        }

        return new AppSettings();
    }

    public bool Save()
    {
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        LastWritePath = null;

        foreach (string path in new[] { PrimaryPath, FallbackPath })
        {
            if (TryWriteAtomic(path, json))
            {
                LastWritePath = path;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 先写同目录的临时文件再原子替换：中途失败不会留下半个 JSON，
    /// 两个实例同时保存时也只会有一个完整结果胜出。
    /// </summary>
    private static bool TryWriteAtomic(string path, string json)
    {
        string temp = path + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // 清不掉也无所谓，下次写会覆盖它
            }

            return false;
        }
    }
}
