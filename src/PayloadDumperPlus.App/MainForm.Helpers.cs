using System.Text;
using PayloadDumperPlus.Core.Download;
using PayloadDumperPlus.Core.Payload;
using PayloadDumperPlus.Core.Pipeline;

namespace PayloadDumperPlus.App;

public sealed partial class MainForm
{

    private void OnGridDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _lastOutputDirectory is null)
            return;

        string name = _grid.Rows[e.RowIndex].Cells["name"].Value as string ?? string.Empty;
        foreach (string candidate in new[] { name, name + ".img" })
        {
            string path = Path.Combine(_lastOutputDirectory, candidate);
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
                return;
            }
        }

        OpenOutputDirectory();
    }

    private void OpenOutputDirectory()
    {
        string? target = _lastOutputDirectory;
        if (target is null || !Directory.Exists(target))
        {
            // 还没提取过：退回界面上填的输出目录
            string typed = _outputDir.Text.Trim();
            if (typed.Length > 0 && Directory.Exists(typed))
                target = typed;
        }

        if (target is null || !Directory.Exists(target))
        {
            MessageBox.Show(
                this,
                "还没有可打开的输出目录。" + Environment.NewLine + Environment.NewLine +
                "选好「输出目录」跑一次提取，完成后点这里就能直接打开；" + Environment.NewLine +
                "输出目录留空时，镜像会放在压缩包同目录的 extracted_<包名> 文件夹里。",
                "打开输出目录",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
    }

    /// <summary>界面上填的「只解这些镜像」，命令行 --parts 优先。</summary>
    private IReadOnlyCollection<string>? ParseOnlyImages()
    {
        if (_forcedPartitions is { Count: > 0 })
        {
            // 线刷包里的镜像都带扩展名（boot -> boot.img），命令行只写分区名时补上
            return _forcedPartitions
                .Select(p => p.Contains('.') ? p : p + ".img")
                .ToArray();
        }

        string text = _onlyImages.Text.Trim();
        if (text.Length == 0)
            return null;

        return text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private void OnBrowseOutputClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "选择输出目录" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _outputDir.Text = dialog.SelectedPath;
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _settings = dialog.Result;

        if (_settings.Save())
            AppendLog($"设置已保存到 {AppSettings.LastWritePath}。");
        else
            AppendLog("配置写入失败（程序目录和 %LOCALAPPDATA% 都不可写），本次修改仍然生效。");
    }

    /// <summary>提取前弹分区选择。返回 null 表示全部，抛异常表示用户取消。</summary>
    private IReadOnlyCollection<string>? PickPartitions(IReadOnlyList<PartitionUpdate> partitions)
    {
        if (_forcedPartitions is { Count: > 0 })
        {
            Volatile.Write(ref _partitionTotal, _forcedPartitions.Count);
            return _forcedPartitions;
        }

        var items = partitions
            .OrderBy(p => p.PartitionName, StringComparer.Ordinal)
            .Select(p => (p.PartitionName, (p.NewPartitionInfo?.Size ?? 0) / 1024.0 / 1024.0))
            .ToList();

        IReadOnlyCollection<string>? picked = ShowPicker("选择要提取的分区",
            "只勾需要的最快：远程包只下载勾中分区的数据块。全选等于整包下载。", items);

        // 这个方法由后台提取线程回调，_partitionTotal 是给 UI 线程算总进度的，用 Volatile 写
        if (picked is { Count: > 0 })
            Volatile.Write(ref _partitionTotal, picked.Count);

        return picked;
    }

    /// <summary>线刷包：先列出开头出现的镜像让用户勾，不勾就不下。</summary>
    private IReadOnlyCollection<string>? PickImages(IReadOnlyList<(string Name, long Size)> entries)
    {
        if (_forcedPartitions is { Count: > 0 })
            return _forcedPartitions;

        // 这个方法会被后台提取线程回调，不能碰控件：只读 UI 线程维护的快照。
        string text = _onlyImagesText.Trim();
        if (text.Length > 0)
            return text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

        var items = entries
            .Select(e => (e.Name, e.Size / 1024.0 / 1024.0))
            .ToList();

        return ShowPicker("选择要解出的镜像",
            "这是包开头的镜像清单（只读了 8MB）。勾中的才会下载并解出，全部命中后自动停止下载。", items);
    }

    private IReadOnlyCollection<string>? ShowPicker(string title, string hint,
        IReadOnlyList<(string Name, double SizeMb)> items)
    {
        if (InvokeRequired)
            return (IReadOnlyCollection<string>?)Invoke(
                new Func<string, string, IReadOnlyList<(string, double)>, IReadOnlyCollection<string>?>(ShowPicker),
                title, hint, items);

        // 等用户勾选的这段时间没有网络活动，先让空闲看门狗停表，选完再重新计时
        Interlocked.Exchange(ref _awaitingInput, 1);
        Interlocked.Exchange(ref _lastProgressTicks, Environment.TickCount64);
        try
        {
            using var dialog = new PickListDialog(title, hint, items);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                throw new OperationCanceledException("用户取消了选择");

            return dialog.Selected;
        }
        finally
        {
            Interlocked.Exchange(ref _awaitingInput, 0);
            Interlocked.Exchange(ref _lastProgressTicks, Environment.TickCount64);
        }
    }

    /// <summary>把异常链摊平，避免用户只看到外层那句「see inner exception」。</summary>
    private static string DescribeFailure(Exception ex)
    {
        var builder = new StringBuilder();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (builder.Length > 0)
                builder.Append(" ← ");

            builder.Append(current.Message);
        }

        string text = builder.ToString();

        if (FindInner<System.Security.Authentication.AuthenticationException>(ex) is not null)
        {
            text += Environment.NewLine + Environment.NewLine
                + "这是 TLS 握手失败，跟分区解析无关。常见原因：" + Environment.NewLine
                + "· 系统代理或加速器拦截了 HTTPS —— 勾上「直连（不用系统代理）」再试" + Environment.NewLine
                + "· 服务器证书链不完整，或证书已过期" + Environment.NewLine
                + "· 系统时间不准，证书有效期校验失败";
        }
        else if (FindInner<System.Net.Sockets.SocketException>(ex) is { } socket)
        {
            text += Environment.NewLine + Environment.NewLine
                + $"网络层失败（{socket.SocketErrorCode}）。先确认链接能直连，或是否需要走代理。";
        }
        else if (FindInner<System.Net.Http.HttpRequestException>(ex) is { } http && http.StatusCode is { } status)
        {
            text += Environment.NewLine + Environment.NewLine
                + $"服务器返回 {(int)status} {status}。链接可能已失效、需要登录，或拒绝当前 UA。";
        }

        return text;
    }

    private static T? FindInner<T>(Exception ex)
        where T : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
                return match;
        }

        return null;
    }

    private static readonly object LogFileLock = new();
    private static string? _logFilePath;

    private void AppendLog(string text)
    {
        if (_log.TextLength > 200_000)
            _log.Clear();

        string line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        _log.AppendText(line + Environment.NewLine);

        try
        {
            _logFilePath ??= Environment.GetEnvironmentVariable("PDP_LOG");
            if (!string.IsNullOrWhiteSpace(_logFilePath))
            {
                lock (LogFileLock)
                    File.AppendAllText(_logFilePath!, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // 日志落盘失败不影响主流程
        }
    }
}
