using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PayloadDumperPlus.Core.Compression;
using PayloadDumperPlus.Core.IO;

namespace PayloadDumperPlus.Core.Payload;

public enum ProgressKind
{
    Log,
    PartitionStarted,
    PartitionBytes,
    PartitionCompleted,
    PartitionFailed,
    Verification,
    Overall,
}

public sealed class ProgressEvent
{
    public ProgressKind Kind { get; init; }
    public string? Partition { get; init; }
    public string? Message { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double BytesPerSecond { get; init; }
}

public sealed class ExtractionOptions
{
    /// <summary>并发操作的线程数（同一时刻在途的 InstallOperation 数量）。</summary>
    public int MaxConcurrency { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 32);

    /// <summary>提取完成后校验分区整块 sha256。</summary>
    public bool VerifyPartitionHash { get; init; } = true;

    /// <summary>校验每个 operation 的 data_sha256_hash（很慢，默认关）。</summary>
    public bool VerifyOperationHash { get; init; }

    /// <summary>只提取这些分区；null 表示全部。</summary>
    public IReadOnlyCollection<string>? OnlyPartitions { get; init; }

    public bool Overwrite { get; init; } = true;

    /// <summary>运行中可变的并发上限。返回 &lt;=0 或 null 时沿用 MaxConcurrency。</summary>
    public Func<int>? ConcurrencyProvider { get; init; }

    /// <summary>
    /// 自动降档的兼容开关：网络下载侧使用它，提取侧当前不做自动降档
    /// （本地提取的瓶颈是磁盘和 xz 解压，跑满核心才最快），设置它不改变提取并发。
    /// </summary>
    public bool AutoDownshift { get; init; } = true;
}

public sealed class PartitionResult
{
    public required string Name { get; init; }
    public long Size { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool? HashVerified { get; init; }
}

public sealed class ExtractionReport
{
    public required IReadOnlyList<PartitionResult> Partitions { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>本次提取实测达到的最大并发 worker 数（诊断用，1 表示退化成串行）。</summary>
    public int PeakConcurrency { get; init; }

    public int SuccessCount => Partitions.Count(p => p.Success && !p.Skipped);

    public int FailedCount => Partitions.Count(p => !p.Success && !p.Skipped);

    public int SkippedCount => Partitions.Count(p => p.Skipped);
}

/// <summary>
/// payload.bin 提取器。
/// 并发模型：所有分区的所有 InstallOperation 进同一个工作队列，由固定数量的 worker 抢取，
/// 每个 worker 用 pread 读自己的 blob 区间、解压、再按 dst_extents 用 pwrite 写目标文件。
/// 读、解压、写全程无共享锁，因此分区级和 operation 级并行是同一套机制。
/// </summary>
public sealed class PayloadExtractor : IDisposable
{
    private readonly IRandomReader _reader;
    private readonly long _dataStart;

    public PayloadHeader Header { get; }
    public DeltaArchiveManifest Manifest { get; }
    public string PayloadPath { get; }

    private PayloadExtractor(string path, IRandomReader reader, PayloadHeader header,
        DeltaArchiveManifest manifest)
    {
        PayloadPath = path;
        _reader = reader;
        _dataStart = header.DataOffset;
        Header = header;
        Manifest = manifest;
    }

    /// <summary>
    /// 打开 payload。baseOffset 用于 payload.bin 内嵌在 zip 里且未压缩的情况，
    /// 可以直接从 zip 中间读取，不需要先把 payload.bin 抽成临时文件。
    /// </summary>
    public static PayloadExtractor Open(string payloadPath, long baseOffset = 0)
    {
        if (!File.Exists(payloadPath))
            throw new FileNotFoundException($"找不到 payload 文件：{payloadPath}", payloadPath);

        var reader = new LocalFileReader(payloadPath, baseOffset);
        try
        {
            return FromReader(reader, payloadPath);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>直接从任意随机读取源打开（本地文件 / HTTP Range 都行）。</summary>
    public static PayloadExtractor Open(IRandomReader reader, string displayName) => FromReader(reader, displayName);

    private static PayloadExtractor FromReader(IRandomReader reader, string displayName)
    {
        // 两步精确读：先 24 字节拿 manifest 长度，再只下 manifest 本身。
        // 走 Stream 会落进 8 MB 分块预取，manifest 只有 1 MB 也要先下满一整块。
        byte[] headBytes = new byte[24];
        if (reader.ReadExact(0, headBytes) < 20)
            throw new InvalidDataException("payload 头部读取失败");

        PayloadHeader header;
        using (var headStream = new MemoryStream(headBytes, writable: false))
            header = PayloadHeader.Read(headStream);

        byte[] manifestBytes = new byte[header.ManifestSize];
        if (manifestBytes.Length > 0 &&
            reader.ReadExact(header.HeaderSize, manifestBytes) < manifestBytes.Length)
            throw new InvalidDataException("payload manifest 读取失败");

        DeltaArchiveManifest manifest = DeltaArchiveManifest.Parse(manifestBytes);

        if (reader.Length < header.DataOffset)
            throw new InvalidDataException("payload 数据被截断：数据区起点超出可用长度");

        return new PayloadExtractor(displayName, reader, header, manifest);
    }

    public IReadOnlyList<PartitionUpdate> Partitions => Manifest.Partitions;

    public async Task<ExtractionReport> ExtractAsync(
        string outputDirectory,
        ExtractionOptions options,
        IProgress<ProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var clock = Stopwatch.StartNew();
        int concurrency = Math.Clamp(options.MaxConcurrency, 1, 32);

        var targets = new List<PartitionWork>();
        foreach (PartitionUpdate partition in Manifest.Partitions)
        {
            if (options.OnlyPartitions is { Count: > 0 } filter &&
                !filter.Contains(partition.PartitionName, StringComparer.OrdinalIgnoreCase))
                continue;

            targets.Add(new PartitionWork(partition));
        }

        long totalBytes = targets.Sum(t => Math.Max(0, t.Partition.OutputSize));

        // 先把每个分区的输出文件建好并预分配，避免 worker 之间抢创建。
        foreach (PartitionWork work in targets)
        {
            PartitionUpdate partition = work.Partition;

            if (partition.RequiresSourceImage)
            {
                work.Skip("增量包（需要旧版镜像作 patch 源），本工具只支持全量包");
                Report(progress, ProgressKind.PartitionFailed, partition.PartitionName, work.Error, 0, totalBytes);
                continue;
            }

            string path = Path.Combine(outputDirectory, partition.PartitionName + ".img");
            if (File.Exists(path) && !options.Overwrite)
            {
                work.Skip("输出文件已存在，跳过");
                Report(progress, ProgressKind.PartitionFailed, partition.PartitionName, work.Error, 0, totalBytes);
                continue;
            }

            try
            {
                work.OpenOutput(path, partition.OutputSize);
                Report(progress, ProgressKind.PartitionStarted, partition.PartitionName, null, 0, totalBytes);
            }
            catch (Exception ex)
            {
                work.Fail($"创建输出文件失败：{ex.Message}");
                Report(progress, ProgressKind.PartitionFailed, partition.PartitionName, work.Error, 0, totalBytes);
            }
        }

        // 组装任务队列：粒度 = 单个 InstallOperation。
        var jobs = new List<(PartitionWork Work, InstallOperation Op)>();
        foreach (PartitionWork work in targets)
        {
            if (!work.IsOpen)
                continue;
            foreach (InstallOperation op in work.Partition.Operations)
                jobs.Add((work, op));
        }

        long bytesDone = 0;
        long lastReportTicks = 0;
        // 每个分区的进度上报时间（Stopwatch ticks）。64 KB 报一次的话 2.97 GB 会报 4.5 万次，
        // 每次都跨线程 BeginInvoke 到 UI，光排队就能把界面拖到卡顿，所以按分区限流。
        ConcurrentDictionary<string, long> lastPartitionReportTicks = new(StringComparer.Ordinal);
        var reportLock = new object();

        // 进度的分母用「要读的 blob 总量」而不是「写出去的分区总量」：
        // 远程提取时读多少 blob 就等于下多少数据，按写出去的分区大小算会早早走满。
        long totalFetch = jobs.Sum(j => (long)j.Op.DataLength);
        long fetchDone = 0;
        if (totalFetch <= 0)
            totalFetch = totalBytes;

        void AddProgress(PartitionWork work, long delta)
        {
            long global;
            long local;
            Interlocked.Add(ref work.BytesDone, delta);
            global = Interlocked.Add(ref bytesDone, delta);
            local = Interlocked.Read(ref work.BytesDone);

            string partitionName = work.Partition.PartitionName;
            long now = clock.ElapsedTicks;

            bool partitionDue = !lastPartitionReportTicks.TryGetValue(partitionName, out long lastPartition)
                                || now - lastPartition > Stopwatch.Frequency / 20;
            if (partitionDue)
            {
                lastPartitionReportTicks[partitionName] = now;
                Report(progress, ProgressKind.PartitionBytes, partitionName, null, local, work.TotalBytes);
            }

            if (now - Interlocked.Read(ref lastReportTicks) > Stopwatch.Frequency / 8)
            {
                lock (reportLock)
                {
                    if (now - lastReportTicks > Stopwatch.Frequency / 8)
                    {
                        lastReportTicks = now;
                        double seconds = clock.Elapsed.TotalSeconds;
                        long fetched = Interlocked.Read(ref fetchDone);
                        Report(progress, ProgressKind.Overall, null, null, fetched, totalFetch,
                            seconds > 0.25 ? fetched / seconds : 0);
                    }
                }
            }
        }

        // 支持运行中调并发：Parallel.ForEachAsync 的并行度启动时就锁死了，
        // 所以这里换成自旋准入——每个任务开跑前读一次目标值，超了就等一会再试。
        // 提取侧的并发上限。本地提取的瓶颈是磁盘和 xz 解压，跑满核心才最快，
        // 所以不做自动降档——自动降档只服务于网络下载。
        // ConcurrencyProvider 是调用方（App 的线程滑块）在开始提取时给出的上限，
        // 运行过程中不再变化。
        int TargetConcurrency() =>
            Math.Clamp(options.ConcurrencyProvider?.Invoke() ?? concurrency, 1, 32);

        // 并发闸门：许可数 = 调度并发。
        // 早先试过"开跑前自旋轮询 20ms"，operation 上千时会产生几万次/秒的调度，
        // 线程池被拖垮，本地提取只有几 MB/s。换成信号量等待，排队不占 CPU。
        int gateCapacity = Math.Clamp(TargetConcurrency(), 1, 32);
        using var gate = new SemaphoreSlim(gateCapacity, gateCapacity);

        // 实测并发峰值：判断调度有没有真的并行，只看这一对计数器
        int liveWorkers = 0;
        int peakWorkers = 0;

        async Task RunJobAsync((PartitionWork Work, InstallOperation Op) job)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            int live = Interlocked.Increment(ref liveWorkers);
            lock (reportLock)
            {
                if (live > peakWorkers)
                    peakWorkers = live;
            }

            try
            {
                PartitionWork work = job.Work;
                if (work.HasFailed || work.IsClosed)
                    return;

                try
                {
                    // 一整块 blob 在慢速直链上要读几十秒，读多少就先报多少，
                    // 让分区进度条在整块读完之前就能动起来。
                    long reported = 0;
                    long written = ProcessOperation(work, job.Op, options, cancellationToken, readSoFar =>
                    {
                        long delta = readSoFar - reported;
                        if (delta > 0)
                        {
                            reported = readSoFar;
                            AddProgress(work, delta);
                        }
                    });

                    Interlocked.Add(ref fetchDone, (long)job.Op.DataLength);

                    // 读入阶段的数字是按比例估的，收尾用真正写出去的量补齐，
                    // 保证累计值和分区总字节精确一致。
                    if (written > reported)
                        AddProgress(work, written - reported);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    work.Fail($"{job.Op.Type} @ data_offset {job.Op.DataOffset}: {ex.Message}");
                    Report(progress, ProgressKind.PartitionFailed, work.Partition.PartitionName, work.Error, 0, totalBytes);
                }
            }
            finally
            {
                Interlocked.Decrement(ref liveWorkers);
                gate.Release();
            }
        }

        // 诊断：把 operation 数量和并发写进日志，慢的时候一眼能看出是解压串行还是调度没跑满
        {
            int opTotal = jobs.Count;
            int bigOps = 0;
            foreach ((PartitionWork w, InstallOperation o) in jobs)
            {
                if (o.DataLength >= 4 * 1024 * 1024)
                    bigOps++;
            }
            Report(progress, ProgressKind.Log, null,
                $"提取调度：{opTotal} 个 operation（其中 ≥4 MB 的 {bigOps} 个），并发 {TargetConcurrency()}",
                0, 0);
        }

        // jobs 是惰性构建的列表，jobs.Select(RunJobAsync) 由单个线程枚举：
        // 闸门许可可用时 WaitAsync 同步完成，任务体会直接在枚举线程上跑完，
        // 整个提取就退化成串行。这里先把 job 物化成数组，再用 Task.Run 派发，
        // 让许可等待发生在异步上下文里，调度并发才真正生效。
        (PartitionWork Work, InstallOperation Op)[] jobArray = jobs.ToArray();
        Task[] workers = new Task[jobArray.Length];
        for (int i = 0; i < jobArray.Length; i++)
        {
            (PartitionWork Work, InstallOperation Op) item = jobArray[i];
            workers[i] = Task.Run(() => RunJobAsync(item), cancellationToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        int peak = Volatile.Read(ref peakWorkers);
        Report(progress, ProgressKind.Log, null,
            $"提取完成：调度并发上限 {gateCapacity}，实测峰值并发 {peak}", 0, 0);

        // 命令行排查用：PDP_DEBUG=1 时把调度结果也打到 stderr（与 SegmentedDownloader 同一约定）
        if (Environment.GetEnvironmentVariable("PDP_DEBUG") == "1")
            Console.Error.WriteLine(
                $"[pdp] 提取并发：{jobs.Count} 个 operation，调度上限 {gateCapacity}，实测峰值 {peak}");

        // 收尾：关闭文件 + 校验
        foreach (PartitionWork work in targets)
        {
            if (!work.IsOpen)
                continue;

            try
            {
                work.CloseOutput();

                if (work.HasFailed)
                    continue;

                // 写到这一步分区就算提取完了，先把完成报出去；
                // 校验是独立的第二阶段，有自己的百分比，不占用提取进度。
                Report(progress, ProgressKind.PartitionCompleted, work.Partition.PartitionName, null,
                    work.TotalBytes, totalBytes);

                bool? verified = null;
                if (options.VerifyPartitionHash && work.Partition.NewPartitionInfo?.Hash is { Length: 32 } expected)
                {
                    byte[] actual = ComputeFileHash(work.OutputPath!, cancellationToken,
                        (done, len) => Report(progress, ProgressKind.Verification,
                            work.Partition.PartitionName, null, done, len));

                    verified = actual.AsSpan().SequenceEqual(expected);
                    if (verified == false)
                    {
                        work.Fail($"sha256 不匹配：期望 {Convert.ToHexString(expected).ToLowerInvariant()}，" +
                                  $"实际 {Convert.ToHexString(actual).ToLowerInvariant()}");
                        Report(progress, ProgressKind.PartitionFailed, work.Partition.PartitionName, work.Error, 0, totalBytes);
                        continue;
                    }

                    Report(progress, ProgressKind.Verification, work.Partition.PartitionName,
                        "校验一致", 1, 1);
                }

                work.Verified = verified;
            }
            catch (Exception ex)
            {
                work.Fail($"收尾失败：{ex.Message}");
                Report(progress, ProgressKind.PartitionFailed, work.Partition.PartitionName, work.Error, 0, totalBytes);
            }
        }

        clock.Stop();

        return new ExtractionReport
        {
            Partitions = targets.Select(t => t.ToResult()).ToList(),
            Elapsed = clock.Elapsed,
            TotalBytes = targets.Where(t => t.IsSuccess).Sum(t => t.TotalBytes),
            PeakConcurrency = peak,
        };
    }

    /// <summary>处理单个 operation，返回写入目标文件的字节数。</summary>
    private long ProcessOperation(PartitionWork work, InstallOperation op, ExtractionOptions options,
        CancellationToken ct, Action<long>? onRead = null)
    {
        ct.ThrowIfCancellationRequested();

        // ZERO/DISCARD：输出文件是刚创建并预分配的，内容本来就是 0，无需落盘。
        if (op.Type is InstallOpType.Zero or InstallOpType.Discard)
            return ExtentBytes(op);

        long expected = ExtentBytes(op);
        if (expected <= 0)
            return 0;

        if (op.DataLength == 0)
            throw new InvalidDataException("operation 没有数据，但类型要求有数据");

        if (op.DataLength > int.MaxValue)
            throw new InvalidDataException($"operation 数据过大（{op.DataLength} 字节）");

        int dataLength = (int)op.DataLength;
        long absoluteOffset = _dataStart + (long)op.DataOffset;

        if (absoluteOffset + dataLength > _reader.Length)
            throw new InvalidDataException(
                $"blob 区间越界：{absoluteOffset}+{dataLength} > 可用长度 {_reader.Length}");

        byte[] compressed = ArrayPool<byte>.Shared.Rent(dataLength);
        byte[]? plain = null;

        try
        {
            // 慢速直链上这一步可能要等几十秒才有整块数据。把「已读入的 blob 字节」
            // 按比例换算成「将写出的目标字节」，读到一段就报一段；
            // 比例只决定中间值，收尾由调用方用真实写入量补齐。
            Action<long, int>? onBytes = null;
            if (onRead is not null && dataLength > 0)
            {
                long seen = 0;
                long spanStart = absoluteOffset;
                long spanEnd = absoluteOffset + dataLength;
                long totalOut = expected;

                Action<long, int> handler = (offset, count) =>
                {
                    long from = Math.Max(offset, spanStart);
                    long to = Math.Min(offset + count, spanEnd);
                    if (to <= from)
                        return;

                    seen += to - from;
                    if (seen > dataLength)
                        seen = dataLength;

                    onRead((long)((double)totalOut * seen / dataLength));
                };

                onBytes = handler;
                _reader.BytesRead += handler;
            }

            try
            {
                ReadExactly(compressed.AsSpan(0, dataLength), absoluteOffset, ct);
            }
            finally
            {
                if (onBytes is not null)
                    _reader.BytesRead -= onBytes;
            }

            if (options.VerifyOperationHash && op.DataSha256 is { Length: 32 } expectedHash)
            {
                byte[] actual = SHA256.HashData(compressed.AsSpan(0, dataLength));
                if (!actual.AsSpan().SequenceEqual(expectedHash))
                    throw new InvalidDataException("operation data 的 sha256 与 manifest 不符");
            }

            ReadOnlySpan<byte> payload;
            switch (op.Type)
            {
                case InstallOpType.Replace:
                    payload = compressed.AsSpan(0, dataLength);
                    break;

                case InstallOpType.ReplaceXz:
                case InstallOpType.ReplaceBz:
                    plain = ArrayPool<byte>.Shared.Rent((int)expected);
                    int plainLength = Decompressor.Decompress(
                        op.Type == InstallOpType.ReplaceXz ? CompressionKind.Xz : CompressionKind.BZip2,
                        compressed, 0, dataLength,
                        plain.AsSpan(0, (int)expected));
                    payload = plain.AsSpan(0, plainLength);
                    break;

                default:
                    throw new NotSupportedException($"不支持的操作类型 {op.Type}");
            }

            if (payload.Length != expected)
                throw new InvalidDataException(
                    $"解出的数据长度 {payload.Length} 与目标 extent 总长 {expected} 不一致");

            long blockSize = Manifest.BlockSize;
            long written = 0;

            // 相邻 extent 在目标文件里首尾相接时合成一次写：小米的包一个 operation 常有
            // 上千个 4 KB extent，一条一条写光系统调用就要几十万次，合并后通常只剩一次。
            int consumed = 0;
            int runStart = 0;
            long runOffset = -1;

            foreach (Extent extent in op.DstExtents)
            {
                int chunk = checked((int)(extent.NumBlocks * (ulong)blockSize));
                if (chunk > payload.Length - consumed)
                    throw new InvalidDataException("extent 长度超过解压数据长度");

                long offset = checked((long)extent.StartBlock * blockSize);

                if (runOffset >= 0 && offset == runOffset + (consumed - runStart))
                {
                    consumed += chunk;
                    written += chunk;
                    continue;
                }

                if (runOffset >= 0 && consumed > runStart)
                    RandomAccess.Write(work.OutputHandle!, payload[runStart..consumed], runOffset);

                runOffset = offset;
                runStart = consumed;
                consumed += chunk;
                written += chunk;
            }

            if (runOffset >= 0 && consumed > runStart)
                RandomAccess.Write(work.OutputHandle!, payload[runStart..consumed], runOffset);

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
            if (plain is not null)
                ArrayPool<byte>.Shared.Return(plain);
        }
    }

    private long ExtentBytes(InstallOperation op)
    {
        long total = 0;
        long blockSize = Manifest.BlockSize;
        foreach (Extent extent in op.DstExtents)
            total += (long)extent.NumBlocks * blockSize;
        return total;
    }

    private void ReadExactly(Span<byte> buffer, long offset, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = _reader.Read(offset + read, buffer[read..], cancellationToken);
            if (n <= 0)
                throw new EndOfStreamException($"读取 payload 数据区失败（偏移 {offset + read}）");
            read += n;
        }
    }

    private static byte[] ComputeFileHash(string path, CancellationToken ct,
        Action<long, long>? onProgress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = stream.Length;
        long done = 0;
        long lastPct = -1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                done += read;

                if (onProgress is not null && total > 0)
                {
                    long pct = done * 100 / total;
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        onProgress(done, total);
                    }
                }
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Report(IProgress<ProgressEvent>? progress, ProgressKind kind, string? partition,
        string? message, long done, long total, double speed = 0)
    {
        progress?.Report(new ProgressEvent
        {
            Kind = kind,
            Partition = partition,
            Message = message,
            BytesDone = done,
            BytesTotal = total,
            BytesPerSecond = speed,
        });
    }

    public void Dispose() => _reader.Dispose();

    private sealed class PartitionWork
    {
        public PartitionWork(PartitionUpdate partition) => Partition = partition;

        public PartitionUpdate Partition { get; }
        public string? OutputPath { get; private set; }
        public SafeFileHandle? OutputHandle { get; private set; }
        public long BytesDone;
        public string? Error { get; private set; }
        public bool? Verified { get; set; }
        public TimeSpan Elapsed { get; set; }

        private long _startedAt;
        private FileStream? _output;

        public bool HasFailed { get; private set; }
        public bool IsClosed { get; private set; }
        public bool IsSkipped { get; private set; }
        public bool IsOpen => _output is not null && !IsClosed;
        public bool IsSuccess => !HasFailed && !IsSkipped && IsClosed;
        public long TotalBytes => Partition.OutputSize;

        public void OpenOutput(string path, long size)
        {
            OutputPath = path;
            _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _output = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            _output.SetLength(size);
            OutputHandle = _output.SafeFileHandle;
        }

        public void CloseOutput()
        {
            if (_output is null)
                return;
            _output.Flush(true);
            _output.Dispose();
            _output = null;
            OutputHandle = null;
            IsClosed = true;

            if (_startedAt != 0)
                Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt);
        }

        public void Fail(string error)
        {
            HasFailed = true;
            Error = error;
        }

        public void Skip(string reason)
        {
            IsSkipped = true;
            Error = reason;
        }

        public PartitionResult ToResult() => new()
        {
            Name = Partition.PartitionName,
            Size = TotalBytes,
            OutputPath = OutputPath ?? string.Empty,
            Success = IsSuccess,
            Skipped = IsSkipped,
            Error = Error,
            Elapsed = Elapsed,
            HashVerified = Verified,
        };
    }
}
