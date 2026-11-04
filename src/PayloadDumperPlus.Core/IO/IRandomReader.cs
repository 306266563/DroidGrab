using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using PayloadDumperPlus.Core.Download;

namespace PayloadDumperPlus.Core.IO;

/// <summary>
/// 可按任意偏移读取的随机读取源。本地文件和远程 HTTP 共用这一个接口，
/// 提取层不需要知道数据到底在哪。
/// </summary>
public interface IRandomReader : IDisposable
{
    long Length { get; }

    int Read(long offset, Span<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取一段元数据（zip 尾部、中央目录、payload 头）。只请求需要的字节，
    /// 不走分块预取——为了看 64 KB 先下 2 MB 在慢速链路上是纯浪费。默认落到 Read。
    /// </summary>
    int ReadExact(long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
        => Read(offset, buffer, cancellationToken);

    /// <summary>
    /// 读取过程中的增量字节上报（本 reader 坐标下的绝对偏移, 字节数）。
    /// 网络分块读取时，一次 Read 调用可能要等若干个 Range 请求，慢速链路上就是几十秒；
    /// 上层订阅它才能把中间进度变成界面事件。本地读取不会长时间没有数据，实现是空订阅。
    /// </summary>
    event Action<long, int>? BytesRead;
}

/// <summary>本地文件。baseOffset 用于 zip 内嵌的 payload.bin，直接从中间读。</summary>
public sealed class LocalFileReader : IRandomReader
{
    private readonly SafeFileHandle _handle;
    private readonly long _baseOffset;

    /// <summary>本地读取每次都是磁盘速度，没有「等几十秒才有进展」的问题，不需要中间上报。</summary>
    public event Action<long, int>? BytesRead
    {
        add { }
        remove { }
    }

    public LocalFileReader(string path, long baseOffset = 0)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        _baseOffset = baseOffset;
        Length = Math.Max(0, RandomAccess.GetLength(_handle) - baseOffset);
    }

    public long Length { get; }

    public int Read(long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset >= Length)
            return 0;

        int want = (int)Math.Min(buffer.Length, Length - offset);
        int read = 0;
        while (read < want)
        {
            int n = RandomAccess.Read(_handle, buffer.Slice(read, want - read), _baseOffset + offset + read);
            if (n <= 0)
                break;
            read += n;
        }

        return read;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// 远程 HTTP 随机读取。按 2MB 分块拉取并做 LRU 缓存，
/// 这样「只提取 boot.img」时只会有几百 KB 的 Range 请求，而不是把整包拖下来。
/// 同时在飞的 Range 请求数由 <see cref="DownloadOptions"/> 的并发设置约束，并按吞吐自动升降档。
/// </summary>
public sealed class HttpRangeReader : IRandomReader
{
    // 块大一点，减少 Range 请求次数；块太小会让每次请求的固定开销占大头
    private const int BlockSize = 2 * 1024 * 1024;
    private const long MaxCacheBytes = 384L * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly string _url;
    private readonly DownloadOptions _options;
    private readonly object _lock = new();

    private readonly Dictionary<int, byte[]> _cache = new();
    private readonly Dictionary<int, long> _lastUse = new();
    private long _tick;
    private long _cachedBytes;

    // 并发闸门：SemaphoreSlim 不支持缩容，缩容时把多余许可「吃掉」并记在 _held 里，
    // 需要扩容时先还回去。四个数各管一件事：
    //   _liveLimit   —— 当前真正放出去的许可数（随滑块和自动档位变化）
    //   _autoCeiling —— 自动调节的当前档位，永远 ≤ 用户设定值
    //   _held        —— 缩容时吃掉并扣着的许可
    //   _peakInFlight—— 实测同时在飞的 Range 请求数峰值（诊断用）
    private readonly SemaphoreSlim _throttle;
    private readonly object _limitLock = new();
    private int _liveLimit;
    private int _held;
    private int _autoCeiling;
    private int _inFlight;
    private int _peakInFlight;

    // 自动升降档的采样状态
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _bytesDone;
    private long _lastSampleBytes;
    private long _lastSampleTicks;
    private double _baseline;
    private bool _hasBaseline;
    private int _lastUserCap;
    private int _lowStreak;
    private int _holdStreak;
    private int _probeFrom;
    private int _probeCooldown;

    // 读取进度订阅：多个提取任务会同时挂上来，增删用锁保护
    private readonly object _progressLock = new();
    private Action<long, int>? _bytesRead;

    public HttpRangeReader(HttpClient client, string url, long length)
        : this(client, url, length, null)
    {
    }

    public HttpRangeReader(HttpClient client, string url, long length, DownloadOptions? options)
    {
        _client = client;
        _url = url;
        _options = options ?? new DownloadOptions();
        Length = length;

        _liveLimit = InitialLimit();
        _autoCeiling = _liveLimit;
        _lastUserCap = _liveLimit;
        _throttle = new SemaphoreSlim(_liveLimit, 32);
        _lastSampleTicks = _clock.ElapsedTicks;
    }

    /// <summary>用户设定的并发上限，可被 PDP_CONNECTIONS 环境变量覆盖（只影响初值）。</summary>
    private int InitialLimit()
    {
        int limit = Math.Clamp(_options.MaxConnections, 1, 32);
        if (int.TryParse(Environment.GetEnvironmentVariable("PDP_CONNECTIONS"), out int forced))
            limit = Math.Clamp(forced, 1, 32);
        return limit;
    }

    /// <summary>
    /// 每收下一个网络数据段就通知一次。一次 Read 可能横跨十几个 Range 请求，
    /// 订阅者靠这些中间点才能看到实时进度，而不是等到整块拉完。
    /// </summary>
    public event Action<long, int>? BytesRead
    {
        add
        {
            lock (_progressLock)
                _bytesRead += value;
        }
        remove
        {
            lock (_progressLock)
                _bytesRead -= value;
        }
    }

    private void RaiseBytesRead(long offset, int count)
    {
        Action<long, int>? handler;
        lock (_progressLock)
            handler = _bytesRead;
        handler?.Invoke(offset, count);
    }

    public long Length { get; }

    public int Read(long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset >= Length || buffer.Length == 0)
            return 0;

        int total = 0;
        while (total < buffer.Length)
        {
            long absolute = offset + total;
            if (absolute >= Length)
                break;

            int blockIndex = (int)(absolute / BlockSize);
            int inBlock = (int)(absolute % BlockSize);
            byte[] block = GetBlock(blockIndex, cancellationToken);

            if (inBlock >= block.Length)
                break;

            int take = Math.Min(buffer.Length - total, block.Length - inBlock);
            block.AsSpan(inBlock, take).CopyTo(buffer[total..]);
            total += take;
        }

        return total;
    }

    public int ReadExact(long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset >= Length || buffer.Length == 0)
            return 0;

        long end = Math.Min(offset + buffer.Length, Length) - 1;
        byte[] data = FetchRange(offset, end, cancellationToken);
        int n = Math.Min(data.Length, buffer.Length);
        data.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    private byte[] GetBlock(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_cache.TryGetValue(index, out byte[]? cached))
            {
                _lastUse[index] = ++_tick;
                return cached;
            }
        }

        long start = (long)index * BlockSize;
        long end = Math.Min(start + BlockSize, Length) - 1;
        byte[] data = FetchRange(start, end, cancellationToken);

        lock (_lock)
        {
            // 并发时可能别的线程已经拉回来了，用现成的
            if (_cache.TryGetValue(index, out byte[]? existing))
                return existing;

            _cache[index] = data;
            _lastUse[index] = ++_tick;
            _cachedBytes += data.Length;
            Evict();
            return data;
        }
    }

    private void Evict()
    {
        while (_cachedBytes > MaxCacheBytes && _cache.Count > 1)
        {
            int victim = -1;
            long oldest = long.MaxValue;

            foreach (KeyValuePair<int, long> entry in _lastUse)
            {
                if (entry.Value < oldest)
                {
                    oldest = entry.Value;
                    victim = entry.Key;
                }
            }

            if (victim < 0)
                return;

            if (_cache.Remove(victim, out byte[]? removed))
                _cachedBytes -= removed.Length;
            _lastUse.Remove(victim);
        }
    }

    /// <summary>单次尝试最多等这么久；每读到一段数据就续期，服务器假死时才真正超时。</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    private byte[] FetchRange(long start, long end, CancellationToken cancellationToken)
    {
        int expected = checked((int)(end - start + 1));
        byte[] buffer = new byte[expected];

        // 慢速链路上单次请求经常被中间设备提前掐断，拿到半截就算失败。
        // 断点续传式重试：已经拿到多少，就从哪里接着要，最多 4 轮。
        int received = 0;
        int attempt = 0;
        while (received < expected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt >= 4)
            {
                throw new IOException(
                    $"远程数据读取不完整：偏移 {start} 需要 {expected} 字节，只拿到 {received} 字节。"
                    + "网络或代理把连接中断了，可以试试换代理节点或稍后重试。");
            }

            attempt++;
            long from = start + received;

            MaybeTune();
            int before = received;
            EnterRequest(cancellationToken);
            try
            {
                using var attemptTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(AttemptTimeout);
                CancellationToken attemptToken = attemptTimeout.Token;

                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(from, end);

                using HttpResponseMessage response = _client.Send(request,
                    HttpCompletionOption.ResponseHeadersRead, attemptToken);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"远程读取失败：HTTP {(int)response.StatusCode}（偏移 {from}）");

                using Stream stream = response.Content.ReadAsStream();
                while (received < expected)
                {
                    attemptToken.ThrowIfCancellationRequested();
                    int n = stream.Read(buffer, received, expected - received);
                    if (n <= 0)
                        break;
                    received += n;
                    // 每读到一段数据就把空闲计时往后推：只有连续 30 秒一个字节都没来才算超时
                    attemptTimeout.CancelAfter(AttemptTimeout);
                    // 慢速链路上这一段可能就是几十秒里的唯一进展，立刻往上报
                    RaiseBytesRead(start + received - n, n);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                if (attempt >= 4)
                {
                    throw new IOException(
                        $"远程读取失败：偏移 {start}，第 {attempt} 次尝试仍然超时或断开。"
                        + "网络或代理长时间没有数据，可以换节点或稍后重试。", ex);
                }
                Thread.Sleep(TimeSpan.FromMilliseconds(500 * attempt));
            }
            finally
            {
                ExitRequest(received - before);
            }
        }

        return buffer;
    }

    /// <summary>用户滑块给出的并发上限（可被 PDP_CONNECTIONS 覆盖），clamp 到 1~32。</summary>
    private int UserCap()
    {
        Func<int>? provider = _options.LiveConnections;
        int wanted;
        try
        {
            wanted = provider is null ? _options.MaxConnections : provider();
        }
        catch
        {
            wanted = _options.MaxConnections;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("PDP_CONNECTIONS"), out int forced))
            wanted = forced;

        return Math.Clamp(wanted, 1, 32);
    }

    /// <summary>
    /// 把许可数调到用户当前设定（自动档位约束之内）。扩容时先还回缩容吃掉的许可，
    /// 缩容时用 Wait(0) 把多余许可收走并记在 _held 里。
    /// </summary>
    private int CurrentLimit()
    {
        int wanted = Math.Min(UserCap(), Math.Max(1, Volatile.Read(ref _autoCeiling)));

        lock (_limitLock)
        {
            while (_liveLimit < wanted)
            {
                if (_held > 0)
                {
                    _throttle.Release(1);
                    _held--;
                }
                _liveLimit++;
            }

            while (_liveLimit > wanted)
            {
                if (!_throttle.Wait(0))
                    break;
                _held++;
                _liveLimit--;
            }

            return _liveLimit;
        }
    }

    /// <summary>取一个并发许可并记录实测峰值。许可只在一次 HTTP 尝试期间持有。</summary>
    private void EnterRequest(CancellationToken cancellationToken)
    {
        CurrentLimit();
        _throttle.Wait(cancellationToken);

        int inFlight = Interlocked.Increment(ref _inFlight);
        lock (_limitLock)
        {
            if (inFlight > _peakInFlight)
                _peakInFlight = inFlight;
        }
    }

    private void ExitRequest(int bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _bytesDone, bytes);

        Interlocked.Decrement(ref _inFlight);
        _throttle.Release();
    }

    /// <summary>
    /// 每 6 秒采样一次吞吐并按需升降档：比基线掉三成、或连续三次低于 400 KB/s 就减半，
    /// 回升两成再逐档加回去；档位永远不超过用户设定。AutoDownshift 关闭时只按用户设定。
    /// </summary>
    private void MaybeTune()
    {
        if (!_options.AutoDownshift)
            return;

        long now = _clock.ElapsedTicks;
        double elapsed = (now - _lastSampleTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed < 6)
            return;

        _lastSampleTicks = now;
        long done = Interlocked.Read(ref _bytesDone);
        double speed = (done - _lastSampleBytes) / elapsed;
        _lastSampleBytes = done;

        if (!_hasBaseline)
        {
            _baseline = speed;
            _hasBaseline = true;
            return;
        }

        int userCap = UserCap();

        if (userCap != _lastUserCap)
        {
            // 用户中途拖了滑块：以新上限为准，重新建立基线
            _lastUserCap = userCap;
            Volatile.Write(ref _autoCeiling, userCap);
            _baseline = speed;
            _probeFrom = 0;
            _probeCooldown = 0;
            _holdStreak = 0;
            _lowStreak = 0;
            return;
        }

        int current = Volatile.Read(ref _autoCeiling);

        // 试探降档还没结算：本轮只结算，不叠加别的调档动作
        if (_probeFrom > 0)
        {
            if (speed < _baseline * 0.9)
            {
                Volatile.Write(ref _autoCeiling, _probeFrom);
                Diag($"probe revert -> {_probeFrom} ({speed / 1048576:F2} MB/s)");
                _options.OnConcurrencyAdjusted?.Invoke(_probeFrom);
            }

            // 探过一次就歇五分钟，别把档位来回拉扯
            _probeCooldown = 50;
            _probeFrom = 0;
            _baseline = speed;
            _holdStreak = 0;
            return;
        }

        // 两条降档理由：
        //   ① 比基线掉了三成——突然变慢了，多半是并发把链路挤坏了
        //   ② 绝对速度就低（低于 400 KB/s）且连续三次采样都这样——慢速 CDN 上
        //      并发越高越慢，这时候基线不会掉，只能看绝对值
        bool dropped = speed < _baseline * 0.7;
        bool stuckLow = current > 1 && speed < 400_000;
        _lowStreak = stuckLow ? _lowStreak + 1 : 0;

        if ((dropped || _lowStreak >= 3) && current > 1)
        {
            int next = Math.Max(1, current / 2);
            Volatile.Write(ref _autoCeiling, next);
            _baseline = speed;
            _lowStreak = 0;
            _holdStreak = 0;
            Diag($"tune down -> {next} ({speed / 1048576:F2} MB/s)");
            _options.OnConcurrencyAdjusted?.Invoke(next);
        }
        else if (speed > _baseline * 1.2 && current < userCap)
        {
            int next = Math.Min(userCap, current + 4);
            Volatile.Write(ref _autoCeiling, next);
            _baseline = speed;
            _lowStreak = 0;
            _holdStreak = 0;
            Diag($"tune up -> {next} ({speed / 1048576:F2} MB/s)");
            _options.OnConcurrencyAdjusted?.Invoke(next);
        }
        else
        {
            // 稳定不动：连续两轮（12 秒）后主动减半试一次，
            // 有些 CDN 从第一秒就慢，光看"比基线掉三成"永远降不下来
            _holdStreak++;
            Diag($"tune hold at {current} ({speed / 1048576:F2} MB/s)");

            if (_holdStreak >= 2 && current >= 3 && _probeCooldown == 0)
            {
                _probeFrom = current;
                int next = Math.Max(1, current / 2);
                Volatile.Write(ref _autoCeiling, next);
                _baseline = speed;
                _holdStreak = 0;
                Diag($"probe down -> {next} ({speed / 1048576:F2} MB/s)");
                _options.OnConcurrencyAdjusted?.Invoke(next);
            }
            else if (_probeCooldown > 0)
            {
                _probeCooldown--;
            }
        }
    }

    private static readonly bool DiagEnabled =
        Environment.GetEnvironmentVariable("PDP_DEBUG") == "1";

    private static void Diag(string message)
    {
        if (DiagEnabled)
            Console.Error.WriteLine($"[pdp] {DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lastUse.Clear();
            _cachedBytes = 0;
        }

        // 只做诊断，不 Dispose _throttle：在飞请求还要 Release 它。
        Diag($"远程读取并发：Range 请求实测峰值 {_peakInFlight}（调度上限 {Math.Max(1, Volatile.Read(ref _autoCeiling))}）");
    }
}

/// <summary>把 IRandomReader 包成只读 Stream，供已有的头部/manifest 解析复用。</summary>
public sealed class RandomReaderStream : Stream
{
    private readonly IRandomReader _reader;
    private long _position;

    public RandomReaderStream(IRandomReader reader) => _reader = reader;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _reader.Length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = _reader.Read(_position, buffer, CancellationToken.None);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
