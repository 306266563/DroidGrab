using PayloadDumperPlus.Core.Archive;
using PayloadDumperPlus.Core.Download;
using PayloadDumperPlus.Core.Payload;
using PayloadDumperPlus.Core.Sparse;

namespace PayloadDumperPlus.Core.Pipeline;

public enum PipelineStage
{
    Idle,
    Probing,
    Downloading,
    Inspecting,
    Extracting,
    Unpacking,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

public sealed class PipelineEvent
{
    public PipelineStage Stage { get; init; }
    public string Message { get; init; } = string.Empty;
    public double Fraction { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public string? Detail { get; init; }

    /// <summary>true = 这是"当前分区"的进度，进度条该跟着它走；false = 总量信息。</summary>
    public bool IsPartitionProgress { get; init; }

    public string? Partition { get; init; }
}

/// <summary>进度出口。用类而不是 Action，是为了让可选参数在内部调用处生效。</summary>
public sealed class ProgressSink
{
    private readonly IProgress<PipelineEvent>? _progress;

    public ProgressSink(IProgress<PipelineEvent>? progress) => _progress = progress;

    public void Report(PipelineStage stage, string message, double fraction,
        long done = 0, long total = 0, double speed = 0, string? detail = null,
        bool isPartitionProgress = false, string? partition = null)
    {
        _progress?.Report(new PipelineEvent
        {
            Stage = stage,
            Message = message,
            Fraction = Math.Clamp(fraction, 0, 1),
            BytesDone = done,
            BytesTotal = total,
            BytesPerSecond = speed,
            Detail = detail,
            IsPartitionProgress = isPartitionProgress,
            Partition = partition,
        });
    }
}

public sealed class PipelineOptions
{
    /// <summary>本地文件路径或 http(s) 链接。</summary>
    public required string Source { get; init; }

    /// <summary>为空时自动使用「包所在目录 / extracted_包名」。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>下载并发。默认按本机核数推荐（核数×2，夹在 8~16），界面可随意调。</summary>
    public int DownloadConnections { get; init; } = Math.Clamp(Environment.ProcessorCount * 2, 8, 16);
    /// <summary>提取并发。默认等于本机核数（解压吃 CPU），界面可随意调。</summary>
    public int ExtractThreads { get; init; } = Math.Clamp(Environment.ProcessorCount, 4, 32);

    /// <summary>运行中实时读取的提取并发（界面滑块），返回 &lt;=0 时用 ExtractThreads。</summary>
    public Func<int>? LiveExtractThreads { get; init; }

    /// <summary>下载并发被自动调档时回调，用来让界面滑块跟着动。</summary>
    public Action<int>? OnDownloadConcurrencyAdjusted { get; init; }

    /// <summary>运行期间读取当前允许的下载连接数；为空时沿用 DownloadConnections。</summary>
    public Func<int>? LiveDownloadThreads { get; init; }

    /// <summary>吞吐下滑时自动降档。</summary>
    public bool AutoDownshift { get; init; } = true;
    public bool VerifyHash { get; init; } = true;
    public bool UnpackSuper { get; init; } = true;
    public bool KeepSuper { get; init; } = true;
    public bool ExpandSparse { get; init; } = true;

    public string? UserAgent { get; init; }
    public string? Referer { get; init; }
    public string? Cookie { get; init; }
    public string? Proxy { get; init; }

    /// <summary>代理模式，默认跟随系统设置。</summary>
    public ProxyMode ProxyMode { get; init; } = ProxyMode.System;

    /// <summary>线刷包流式解包时点名要的镜像文件名（null = 全部）。</summary>
    public IReadOnlyCollection<string>? OnlyImages { get; init; }

    /// <summary>
    /// 线刷包预览回调：先拉开头几 MB 列出已出现的镜像，让用户挑。
    /// 返回 null 表示全都要。
    /// </summary>
    public Func<IReadOnlyList<(string Name, long Size)>, IReadOnlyCollection<string>?>? ImageSelector { get; init; }

    /// <summary>
    /// 提取前回调：拿到分区清单，返回要提取的分区名（null = 全部）。
    /// 远程模式下这一步很关键——不选的分区连数据块都不会去拉。
    /// </summary>
    public Func<IReadOnlyList<PartitionUpdate>, IReadOnlyCollection<string>?>? PartitionSelector { get; init; }

}

public sealed class PipelineResult
{
    public PackageKind Kind { get; init; }
    public required string OutputDirectory { get; init; }
    public IReadOnlyList<PartitionResult> Partitions { get; init; } = Array.Empty<PartitionResult>();
    public IReadOnlyList<LineFlashEntry> Images { get; init; } = Array.Empty<LineFlashEntry>();
    public IReadOnlyList<string> SuperPartitions { get; init; } = Array.Empty<string>();
    public TimeSpan Elapsed { get; init; }
    public long TotalBytes { get; init; }
    public string? LocalPackagePath { get; init; }

    public int PartitionSuccessCount => Partitions.Count(p => p.Success && !p.Skipped);
    public int PartitionFailedCount => Partitions.Count(p => !p.Success && !p.Skipped);
}

/// <summary>
/// 把「链接/本地文件 → 识别 → 下载 → 解包 → 提取 → 拆分」串成一条流水线。
/// GUI 只需要传 Source 和一个进度回调。
/// </summary>
public static class ExtractPipeline
{
    public static bool LooksLikeUrl(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static async Task<PipelineResult> RunAsync(
        PipelineOptions options,
        IProgress<PipelineEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sink = new ProgressSink(progress);

        try
        {
            string localPath;

            if (LooksLikeUrl(options.Source))
            {
                DownloadOptions downloadOptions = BuildDownloadOptions(options);

                sink.Report(PipelineStage.Probing, "正在连接服务器…", 0);
                using (var prober = new SegmentedDownloader(downloadOptions))
                {
                    DownloadProbe probe = await prober.ProbeAsync(options.Source, cancellationToken)
                        .ConfigureAwait(false);

                    string remoteName = probe.SuggestedFileName ?? GuessFileName(probe.FinalUrl);
                    string remoteOutput = ResolveOutputDirectory(options, remoteName);

                    // 卡刷包（payload.bin / OTA zip）：payload 在远端是未压缩存放的，
                    // 只拉 manifest 和真正要提取的那部分数据，不下载整包。
                    try
                    {
                        using RemotePayloadLocation remote = await RemotePackage
                            .OpenAsync(downloadOptions, probe.FinalUrl, probe.TotalBytes, cancellationToken)
                            .ConfigureAwait(false);

                        sink.Report(PipelineStage.Inspecting, remote.Description, 0.2);

                        using PayloadExtractor remoteExtractor =
                            PayloadExtractor.Open(remote.PayloadReader, remote.Description);

                        int remoteThreads = Math.Min(32,
                            Math.Max(options.ExtractThreads, Environment.ProcessorCount * 2));

                        var (partitions, bytes) = await ExtractPayloadCoreAsync(
                            remoteExtractor, remoteOutput, options, sink, cancellationToken, 0.02, remoteThreads)
                            .ConfigureAwait(false);

                        clock.Stop();
                        sink.Report(PipelineStage.Completed, "完成", 1);
                        return new PipelineResult
                        {
                            Kind = remote.Kind,
                            OutputDirectory = remoteOutput,
                            Partitions = partitions,
                            Elapsed = clock.Elapsed,
                            TotalBytes = bytes,
                            LocalPackagePath = probe.FinalUrl,
                        };
                    }
                    catch (InvalidDataException ex)
                    {
                        // 线刷包 tar/tgz：gzip 是流式压缩，没法按偏移跳读。
                        // 但可以边下边解——不落地整包，下载和解压并行，点名要的镜像拿到就能停。
                        sink.Report(PipelineStage.Inspecting, $"按需直读不可用：{ex.Message}，改用边下边解", 0.05);

                        var (streamed, written) = await ExtractStreamingAsync(
                            downloadOptions, probe, remoteOutput, options, sink, cancellationToken).ConfigureAwait(false);

                        clock.Stop();
                        sink.Report(PipelineStage.Completed, "完成", 1);
                        return new PipelineResult
                        {
                            Kind = PackageKind.LineFlashArchive,
                            OutputDirectory = remoteOutput,
                            Images = streamed.Images,
                            Elapsed = clock.Elapsed,
                            TotalBytes = written,
                            LocalPackagePath = probe.FinalUrl,
                        };
                    }
                }

                // 远程分支到此为止：按需直读和边下边解两条路径都以 return 结束，
                // 其它异常直接向上抛，不存在「先整包下载再本地解」的兜底路径。
            }
            else if (!File.Exists(options.Source))
            {
                throw new FileNotFoundException($"找不到文件：{options.Source}");
            }
            else
            {
                localPath = options.Source;
            }

            sink.Report(PipelineStage.Inspecting, "识别包类型…", 0.55);

            PackageKind kind = PackageInspector.Detect(localPath);
            string outputDirectory = ResolveOutputDirectory(options, localPath);

            switch (kind)
            {
                case PackageKind.PayloadBin:
                case PackageKind.OtaZip:
                {
                    var (partitions, bytes) = await ExtractPayloadAsync(
                        localPath, outputDirectory, options, sink, cancellationToken).ConfigureAwait(false);

                    clock.Stop();
                    sink.Report(PipelineStage.Completed, "完成", 1);
                    return new PipelineResult
                    {
                        Kind = kind,
                        OutputDirectory = outputDirectory,
                        Partitions = partitions,
                        Elapsed = clock.Elapsed,
                        TotalBytes = bytes,
                        LocalPackagePath = localPath,
                    };
                }

                case PackageKind.LineFlashArchive:
                case PackageKind.SparseImage:
                {
                    var (report, bytes) = await ExtractLineFlashAsync(
                        localPath, outputDirectory, options, sink, cancellationToken).ConfigureAwait(false);

                    clock.Stop();
                    sink.Report(PipelineStage.Completed, "完成", 1);
                    return new PipelineResult
                    {
                        Kind = kind,
                        OutputDirectory = outputDirectory,
                        Images = report.Images,
                        SuperPartitions = report.SuperPartitions,
                        Elapsed = clock.Elapsed,
                        TotalBytes = bytes,
                        LocalPackagePath = localPath,
                    };
                }

                default:
                    throw new InvalidDataException(
                        "无法识别的包：既不是 payload.bin、也不是 OTA zip 或线刷包（tar/tgz/zip）");
            }
        }
        catch (OperationCanceledException)
        {
            sink.Report(PipelineStage.Cancelled, "已取消", 0);
            throw;
        }
        catch (Exception ex)
        {
            sink.Report(PipelineStage.Failed, ex.Message, 0);
            throw;
        }
    }

    private static async Task<(HttpStreamingExtractor.Result Result, long Bytes)> ExtractStreamingAsync(
        DownloadOptions downloadOptions, DownloadProbe probe, string outputDirectory,
        PipelineOptions options, ProgressSink sink, CancellationToken cancellationToken)
    {
        sink.Report(PipelineStage.Downloading,
            $"边下边解 {probe.SuggestedFileName ?? GuessFileName(probe.FinalUrl)}（{FormatSize(probe.TotalBytes)}）",
            0.1, 0, probe.TotalBytes);

        IReadOnlyCollection<string>? only = options.OnlyImages;

        // 没点名要哪些就先把清单拉出来让用户勾，别默认闷头下 10GB
        if (only is null && options.ImageSelector is not null)
        {
            sink.Report(PipelineStage.Inspecting, "正在读取镜像清单…", 0.05);
            using HttpClient previewClient = RemotePackage.CreateClient(downloadOptions);
            List<(string Name, long Size)> entries = await HttpStreamingExtractor
                .PreviewAsync(previewClient, probe.FinalUrl, 8L * 1024 * 1024, cancellationToken)
                .ConfigureAwait(false);

            if (entries.Count > 0)
            {
                var listed = entries
                    .Select(e => (Name: Path.GetFileName(e.Name), e.Size))
                    .ToList();
                only = options.ImageSelector(listed);
            }
        }

        using HttpClient client = RemotePackage.CreateClient(downloadOptions);

        // 流式解包的进度回调里没有人算速度，这里用 0.5 秒滑动窗口补一个瞬时值
        var streamClock = System.Diagnostics.Stopwatch.StartNew();
        long speedLastBytes = 0;
        double speedLastSeconds = 0;
        double speedShown = 0;

        HttpStreamingExtractor.Result result = await HttpStreamingExtractor.ExtractAsync(
            client,
            probe.FinalUrl,
            probe.TotalBytes,
            outputDirectory,
            only,
            (name, done, total) =>
            {
                double now = streamClock.Elapsed.TotalSeconds;
                double delta = now - speedLastSeconds;
                if (delta >= 0.5)
                {
                    double sampled = Math.Max(0, (done - speedLastBytes) / delta);
                    // 采样窗口内没有新数据时沿用上一值，别让速度在 0 和真实值之间来回跳
                    if (sampled > 0)
                        speedShown = sampled;
                    speedLastBytes = done;
                    speedLastSeconds = now;
                }

                sink.Report(PipelineStage.Downloading,
                    $"{SpeedPrefix(speedShown)}已下载 {FormatSize(done)} / 总 {FormatSize(total)}（压缩数据）  ·  {name}",
                    total > 0 ? 0.1 + 0.7 * ((double)done / total) : 0.4, done, total, speedShown);
            },
            message => sink.Report(PipelineStage.Extracting, message, 0.85),
            cancellationToken).ConfigureAwait(false);

        return (result, result.WrittenBytes);
    }

    private static DownloadOptions BuildDownloadOptions(PipelineOptions options) => new()
    {
        MaxConnections = Math.Clamp(options.DownloadConnections, 1, 32),
        LiveConnections = options.LiveDownloadThreads,
        OnConcurrencyAdjusted = options.OnDownloadConcurrencyAdjusted,
        AutoDownshift = options.AutoDownshift,
        UserAgent = string.IsNullOrWhiteSpace(options.UserAgent)
            ? new DownloadOptions().UserAgent
            : options.UserAgent!,
        Referer = options.Referer,
        Cookie = options.Cookie,
        Proxy = options.Proxy,
        ProxyMode = options.ProxyMode,
    };

    private static async Task<(IReadOnlyList<PartitionResult> Partitions, long Bytes)> ExtractPayloadAsync(
        string packagePath, string outputDirectory, PipelineOptions options, ProgressSink sink,
        CancellationToken cancellationToken)
    {
        PayloadLocation location = PackageInspector.ResolvePayload(packagePath);
        try
        {
            sink.Report(PipelineStage.Inspecting, location.Description, 0.55);
            using PayloadExtractor extractor = PayloadExtractor.Open(location.FilePath, location.Offset);
            return await ExtractPayloadCoreAsync(extractor, outputDirectory, options, sink, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            location.Cleanup();
        }
    }

    private static async Task<(IReadOnlyList<PartitionResult> Partitions, long Bytes)> ExtractPayloadCoreAsync(
        PayloadExtractor extractor, string outputDirectory, PipelineOptions options, ProgressSink sink,
        CancellationToken cancellationToken, double progressStart = 0.55, int? threadOverride = null)
    {
        sink.Report(PipelineStage.Inspecting,
            $"payload v{extractor.Header.FormatVersion}，{extractor.Manifest.Partitions.Count} 个分区，" +
            $"block_size={extractor.Manifest.BlockSize}",
            0.3);

        long lastPartitionReport = 0;

        var payloadProgress = new Progress<ProgressEvent>(e =>
        {
            switch (e.Kind)
            {
                case ProgressKind.PartitionStarted:
                    sink.Report(PipelineStage.Extracting, $"正在提取 {e.Partition}", progressStart, 0, 0, 0, null, true, e.Partition);
                    break;

                case ProgressKind.PartitionBytes:
                {
                    // 分区级进度：进度条跟着当前这个分区走，换分区就从头开始
                    long now = Environment.TickCount64;
                    if (now - Interlocked.Read(ref lastPartitionReport) < 120)
                        break;
                    Interlocked.Exchange(ref lastPartitionReport, now);

                    double partRatio = e.BytesTotal > 0 ? (double)e.BytesDone / e.BytesTotal : 0;
                    // 只报分区名，字节数和速度交给下面那行，避免两行显示同一组数字
                    sink.Report(PipelineStage.Extracting,
                        $"正在提取 {e.Partition}   {FormatSize(e.BytesDone)} / {FormatSize(e.BytesTotal)}",
                        progressStart + (0.98 - progressStart) * partRatio,
                        e.BytesDone, e.BytesTotal, 0, null, true, e.Partition);
                    break;
                }

                case ProgressKind.PartitionCompleted:
                    // 提取完成：通知界面把这一行收成「完成」，校验另算
                    sink.Report(PipelineStage.Extracting, $"✓ {e.Partition} 提取完成", 0.99, 0, 0, 0, null, true, e.Partition);
                    break;
                case ProgressKind.Verification:
                {
                    // 校验是独立阶段：只报自己的百分比，不碰提取进度条
                    double pct = e.BytesTotal > 0 ? (double)e.BytesDone * 100 / e.BytesTotal : 0;
                    string text = e.Message is { Length: > 0 }
                        ? $"校验 {e.Partition} {e.Message}"
                        : $"校验 {e.Partition} {pct:F1}%（{FormatSize(e.BytesDone)} / {FormatSize(e.BytesTotal)}）";
                    sink.Report(PipelineStage.Verifying, text, 0.99, e.BytesDone, e.BytesTotal);
                    break;
                }

                case ProgressKind.Log:
                    // 提取器给出的诊断行（调度规模、实测并发峰值）原样透出，
                    // 排查「并发没跑满」时是第一条线索
                    if (e.Message is { Length: > 0 })
                        sink.Report(PipelineStage.Extracting, e.Message, progressStart);
                    break;

                case ProgressKind.PartitionFailed:
                    sink.Report(PipelineStage.Extracting, $"✗ {e.Partition}：{e.Message}", 0.6);
                    break;
                case ProgressKind.Overall:
                    double ratio = e.BytesTotal > 0 ? (double)e.BytesDone / e.BytesTotal : 0;
                    double fraction = progressStart + (0.98 - progressStart) * ratio;
                    sink.Report(PipelineStage.Extracting,
                        $"{SpeedPrefix(e.BytesPerSecond)}已下载 {FormatSize(e.BytesDone)} / {FormatSize(e.BytesTotal)}",
                        fraction, e.BytesDone, e.BytesTotal, e.BytesPerSecond);
                    break;
            }
        });

        IReadOnlyCollection<string>? only = options.PartitionSelector?.Invoke(extractor.Manifest.Partitions);
        if (only is { Count: 0 })
            only = null;

        // 远程直读时这些线程主要在等网络，不是纯 CPU 活，按核数给会自我限流
        int threads = threadOverride ?? options.ExtractThreads;

        ExtractionReport extraction = await extractor.ExtractAsync(outputDirectory, new ExtractionOptions
        {
            MaxConcurrency = Math.Clamp(threads, 1, 32),
            ConcurrencyProvider = options.LiveExtractThreads,
            AutoDownshift = options.AutoDownshift,
            VerifyPartitionHash = options.VerifyHash,
            OnlyPartitions = only,
        }, payloadProgress, cancellationToken).ConfigureAwait(false);

        return (extraction.Partitions, extraction.TotalBytes);
    }

    private static async Task<(LineFlashReport Report, long Bytes)> ExtractLineFlashAsync(
        string packagePath, string outputDirectory, PipelineOptions options, ProgressSink sink,
        CancellationToken cancellationToken)
    {
        if (options.ExpandSparse && PackageInspector.Detect(packagePath) == PackageKind.SparseImage)
        {
            string name = Path.GetFileNameWithoutExtension(packagePath) + ".img";
            string target = Path.Combine(outputDirectory, name);

            sink.Report(PipelineStage.Unpacking, $"展开 sparse 镜像 → {name}", 0.6);

            using SparseImage sparse = SparseImage.Open(packagePath);
            sparse.ExpandTo(target, (done, total) =>
            {
                double fraction = 0.5 + 0.5 * (total > 0 ? (double)done / total : 0);
                sink.Report(PipelineStage.Unpacking, $"展开中 {FormatSize(done)} / {FormatSize(total)}",
                    fraction, done, total);
            }, cancellationToken);

            return (new LineFlashReport { Images = Array.Empty<LineFlashEntry>() }, sparse.Size);
        }

        sink.Report(PipelineStage.Extracting, "解包线刷包…", 0.55);

        LineFlashReport lineReport = await Task.Run(() => LineFlashExtractor.Extract(
            packagePath,
            outputDirectory,
            new LineFlashOptions
            {
                UnpackSuper = options.UnpackSuper,
                KeepSuper = options.KeepSuper,
                ExpandSparse = options.ExpandSparse,
            },
            (name, done, total) =>
            {
                double fraction = total > 0 ? 0.55 + 0.4 * ((double)done / total) : 0.6;
                sink.Report(PipelineStage.Extracting, $"{name}  {FormatSize(done)} / {FormatSize(total)}",
                    fraction, done, total);
            },
            cancellationToken), cancellationToken).ConfigureAwait(false);

        return (lineReport, lineReport.TotalBytes);
    }

    private static string ResolveOutputDirectory(PipelineOptions options, string localPath)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            Directory.CreateDirectory(options.OutputDirectory);
            return options.OutputDirectory;
        }

        string baseName = Path.GetFileNameWithoutExtension(localPath);
        if (baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            baseName = Path.GetFileNameWithoutExtension(baseName);

        string parent = Path.IsPathRooted(localPath)
            ? Path.GetDirectoryName(Path.GetFullPath(localPath)) ?? Directory.GetCurrentDirectory()
            : Path.Combine(Path.GetTempPath(), "PayloadDumperPlus");
        string directory = Path.Combine(parent, "extracted_" + Sanitize(baseName));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GuessFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            string name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
        }
        catch (UriFormatException)
        {
            return "download.bin";
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "download.bin" : name;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>速度为 0 时不要输出「- ·」，直接省略速度段。</summary>
    public static string SpeedPrefix(double bytesPerSecond) =>
        bytesPerSecond >= 1024 ? FormatSpeed(bytesPerSecond) + " · " : string.Empty;

    public static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "-" : $"{FormatSize((long)bytesPerSecond)}/s";
}
