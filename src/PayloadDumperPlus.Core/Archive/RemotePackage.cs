using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PayloadDumperPlus.Core.Download;
using PayloadDumperPlus.Core.IO;

namespace PayloadDumperPlus.Core.Archive;

/// <summary>在已有读取源上套一层偏移窗口，用于读取 zip 内部某个条目。</summary>
public sealed class SubReader : IRandomReader
{
    private readonly IRandomReader _inner;
    private readonly long _offset;

    // 内层报告的偏移是本 reader 的偏移加上 _offset，转发时要减回去；
    // 用字典记住包装后的委托，否则 -= 找不到那个实例。
    private readonly object _forwardLock = new();
    private readonly Dictionary<Action<long, int>, Action<long, int>> _forwarders = new();

    public SubReader(IRandomReader inner, long offset, long length)
    {
        _inner = inner;
        _offset = offset;
        Length = length;
    }

    /// <summary>
    /// 把内层（HTTP 分块读取）的进度转发给订阅者，偏移换算到本 reader 的坐标系。
    /// 块对齐可能让内层报出落在本区间之外的数据，订阅者自己按区间过滤。
    /// </summary>
    public event Action<long, int>? BytesRead
    {
        add
        {
            if (value is null)
                return;

            long shift = _offset;
            Action<long, int> forward = (offset, count) => value(offset - shift, count);
            lock (_forwardLock)
                _forwarders[value] = forward;
            _inner.BytesRead += forward;
        }
        remove
        {
            if (value is null)
                return;

            Action<long, int>? forward = null;
            lock (_forwardLock)
            {
                if (_forwarders.TryGetValue(value, out forward))
                    _forwarders.Remove(value);
            }

            if (forward is not null)
                _inner.BytesRead -= forward;
        }
    }

    public long Length { get; }

    public int Read(long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset >= Length)
            return 0;

        int want = (int)Math.Min(buffer.Length, Length - offset);
        return _inner.Read(_offset + offset, buffer[..want], cancellationToken);
    }

    public int ReadExact(long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset >= Length)
            return 0;

        int want = (int)Math.Min(buffer.Length, Length - offset);
        return _inner.ReadExact(_offset + offset, buffer[..want], cancellationToken);
    }

    public void Dispose()
    {
        // 内层由调用方持有
    }
}

public sealed class RemotePayloadLocation : IDisposable
{
    public required PackageKind Kind { get; init; }
    public required IRandomReader PayloadReader { get; init; }
    public HttpClient? Client { get; init; }
    public required string Description { get; init; }
    /// <summary>payload 在远端的长度。</summary>
    public long PayloadLength { get; init; }

    public void Dispose()
    {
        PayloadReader.Dispose();
        Client?.Dispose();
    }
}

/// <summary>
/// 远程包定位：只拉必要的几个小块（zip 尾部、中央目录、local header），
/// 就能算出 payload.bin 在远端文件里的偏移，后续提取直接对这个区间做 Range 读。
/// </summary>
public static class RemotePackage
{
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;
    private const uint LocalFileHeaderSignature = 0x04034b50;

    public static HttpClient CreateClient(DownloadOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            // 这是连接池"护栏"，不是"实际并发"：真正的并发由提取线程/下载线程决定。
            // 护栏设成 64 是为了让它永远不成为瓶颈，而不是偷偷限制用户。
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        if (options.ProxyMode == ProxyMode.Direct)
            handler.UseProxy = false;
        else if (options.ProxyMode == ProxyMode.Custom && !string.IsNullOrWhiteSpace(options.Proxy))
            handler.Proxy = new WebProxy(options.Proxy);

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return client;
    }

    /// <summary>打开远程包：探测类型并定位 payload。非 payload 类包返回 Kind 供上层分流。</summary>
    public static async Task<RemotePayloadLocation> OpenAsync(
        DownloadOptions options, string url, long totalBytes, CancellationToken cancellationToken)
    {
        HttpClient client = CreateClient(options);

        // 先单要 1 KB 判类型。不能走 HttpRangeReader.Read：它按 2 MB 分块，
        // 为了看 4 个字节要先等一整块 2 MB，慢网下就是十几秒的空等。
        byte[] head = new byte[1024];
        int read = 0;
        using (var headRequest = new HttpRequestMessage(HttpMethod.Get, url))
        {
            headRequest.Headers.Range = new RangeHeaderValue(0, head.Length - 1);
            using HttpResponseMessage headResponse = await client
                .SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!headResponse.IsSuccessStatusCode)
                throw new HttpRequestException($"远程读取失败：HTTP {(int)headResponse.StatusCode}（读取文件头）");

            using Stream headStream = await headResponse.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            while (read < head.Length)
            {
                int n = await headStream.ReadAsync(head.AsMemory(read), cancellationToken)
                    .ConfigureAwait(false);
                if (n <= 0)
                    break;
                read += n;
            }
        }

        var reader = new HttpRangeReader(client, url, totalBytes, options);

        try
        {

            if (read >= 4 && head[0] == (byte)'C' && head[1] == (byte)'r' && head[2] == (byte)'A' && head[3] == (byte)'U')
            {
                return new RemotePayloadLocation
                {
                    Kind = PackageKind.PayloadBin,
                    PayloadReader = reader,
                    Description = "远程 payload.bin 直读",
                    PayloadLength = totalBytes,
                    Client = client,
                };
            }

            uint magic = read >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(head) : 0u;
            if (magic == 0x04034B50 || magic == EocdSignature || magic == 0x08074B50)
            {
                if (TryLocatePayloadInZip(reader, out long dataOffset, out long size, out string entryName))
                {
                    return new RemotePayloadLocation
                    {
                        Kind = PackageKind.OtaZip,
                        PayloadReader = new SubReader(reader, dataOffset, size),
                        Description = $"远程 zip 内 {entryName}（只读该条目，{size / 1024.0 / 1024:F1} MB）",
                        PayloadLength = size,
                        Client = client,
                    };
                }

                throw new InvalidDataException("远程 zip 里找不到 payload.bin（可能是线刷包，需要顺序解压）");
            }

            throw new InvalidDataException(
                "远程包不是 payload.bin 或 OTA zip。线刷包 tgz 是 gzip 流，无法按需跳读");
        }
        catch
        {
            reader.Dispose();
            client.Dispose();
            throw;
        }
    }

    private static bool TryLocatePayloadInZip(IRandomReader reader, out long dataOffset, out long size, out string entryName)
    {
        dataOffset = 0;
        size = 0;
        entryName = string.Empty;

        int tailSize = (int)Math.Min(reader.Length, 65557);
        byte[] tail = new byte[tailSize];
        int tailRead = reader.ReadExact(reader.Length - tailSize, tail);
        if (tailRead != tailSize)
            return false;

        int eocd = FindBackwards(tail, EocdSignature);
        if (eocd < 0)
            return false;

        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));

        if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
        {
            long locatorOffset = reader.Length - tailSize + eocd - 20;
            if (locatorOffset < 0)
                return false;

            byte[] locator = new byte[20];
            if (reader.ReadExact(locatorOffset, locator) != 20)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
                return false;

            long zip64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            byte[] record = new byte[56];
            if (reader.ReadExact(zip64Offset, record) != 56)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EocdSignature)
                return false;

            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        }

        if (cdSize <= 0 || cdSize > 64 * 1024 * 1024 || cdOffset + cdSize > reader.Length)
            return false;

        byte[] cd = new byte[cdSize];
        if (reader.ReadExact(cdOffset, cd) != cdSize)
            return false;

        int position = 0;
        while (position + 46 <= cd.Length)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position)) != CentralFileHeaderSignature)
                break;

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 10));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position + 20));
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position + 24));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 28));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 30));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 32));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position + 42));

            string name = Encoding.UTF8.GetString(cd, position + 46, nameLength).Replace('\\', '/');

            bool isPayload = name.Equals("payload.bin", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith("/payload.bin", StringComparison.OrdinalIgnoreCase);

            if (isPayload)
            {
                if (method != 0)
                    throw new InvalidDataException(
                        $"远程 zip 里的 {name} 是压缩存放（method {method}），无法按偏移直读，请改为整包下载");

                byte[] localHeader = new byte[30];
                if (reader.Read(localOffset, localHeader) != 30)
                    return false;
                if (BinaryPrimitives.ReadUInt32LittleEndian(localHeader) != LocalFileHeaderSignature)
                    return false;

                int localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(26));
                int localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(28));

                entryName = name;
                dataOffset = localOffset + 30 + localNameLength + localExtraLength;
                size = uncompressed > 0 ? uncompressed : compressed;
                return true;
            }

            position += 46 + nameLength + extraLength + commentLength;
        }

        return false;
    }

    private static int FindBackwards(byte[] buffer, uint signature)
    {
        for (int i = buffer.Length - 4; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) == signature)
                return i;
        }
        return -1;
    }
}
