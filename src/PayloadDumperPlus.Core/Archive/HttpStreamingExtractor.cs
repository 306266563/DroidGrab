using System.Net.Http.Headers;
using SharpCompress.Readers;
using SharpCompress.Readers.Tar;

namespace PayloadDumperPlus.Core.Archive;

/// <summary>
/// 线刷包（tar / tar.gz）的流式提取：一边下载一边解，不落地整包。
/// gzip 是流式压缩，无法随机跳读，所以只能顺序解——但可以做到
/// 「下载和解压并行」「只要前面几个镜像就提前收工」「不占一份 10GB 磁盘」。
/// </summary>
public sealed class HttpStreamingExtractor
{
    private static readonly string[] ImageSuffixes =
    {
        ".img", ".img.br", ".bin", ".mbn", ".elf", ".xml", ".txt",
    };

    public sealed class Result
    {
        public List<LineFlashEntry> Images { get; } = new();
        public long DownloadedBytes { get; set; }
        public long WrittenBytes { get; set; }
        public bool StoppedEarly { get; set; }
    }

    /// <summary>
    /// 从 http(s) 流式解出线刷包里的镜像。
    /// onlyImages 非空时只写这些名字（不区分大小写），全部命中后立即中断下载。
    /// </summary>
    public static async Task<Result> ExtractAsync(
        HttpClient client,
        string url,
        long totalBytes,
        string outputDirectory,
        IReadOnlyCollection<string>? onlyImages,
        Action<string, long, long>? onProgress,
        Action<string>? onLog,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var result = new Result();
        var wanted = onlyImages is { Count: > 0 }
            ? new HashSet<string>(onlyImages, StringComparer.OrdinalIgnoreCase)
            : null;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载失败：HTTP {(int)response.StatusCode}");

        await using Stream raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var counting = new CountingStream(raw, totalBytes, result, onProgress, cancellationToken);

        using IReader reader = ReaderFactory.OpenReader(counting, new ReaderOptions { LeaveStreamOpen = true });

        byte[] buffer = new byte[1 << 20];

        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.Entry.IsDirectory)
                continue;

            string name = (reader.Entry.Key ?? string.Empty).Replace('\\', '/');
            string fileName = Path.GetFileName(name);
            if (fileName.Length == 0)
                continue;

            string lower = name.ToLowerInvariant();
            if (lower.Contains("/meta-inf/") || lower.StartsWith("meta-inf/"))
                continue;

            bool isImage = ImageSuffixes.Any(s => lower.EndsWith(s));
            if (!isImage)
                continue;

            // 用户点名要的镜像才算命中；没点名就全解
            bool selected = wanted is null || wanted.Contains(fileName);
            if (!selected)
            {
                // 不写盘，但流还是得走完才能到下一个条目
                using Stream skip = reader.OpenEntryStream();
                while (skip.Read(buffer, 0, buffer.Length) > 0)
                    cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            var entry = new LineFlashEntry
            {
                EntryName = name,
                Size = reader.Entry.Size,
                OutputName = fileName,
            };
            result.Images.Add(entry);

            long entryStarted = Environment.TickCount64;
            try
            {
                string outputPath = Path.Combine(outputDirectory, fileName);
                int suffix = 1;
                while (File.Exists(outputPath))
                    outputPath = Path.Combine(outputDirectory,
                        $"{Path.GetFileNameWithoutExtension(fileName)}_{suffix++}{Path.GetExtension(fileName)}");

                entry.OutputName = Path.GetFileName(outputPath);

                using Stream entryStream = reader.OpenEntryStream();
                using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    int read;
                    while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        result.WrittenBytes += read;
                        onProgress?.Invoke(name, result.DownloadedBytes, totalBytes);
                    }
                }

                entry.Extracted = true;
                entry.Elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - entryStarted);
                onLog?.Invoke($"解出 {entry.OutputName}（{entry.Size / 1024.0 / 1024:F1} MB，" +
                              $"用时 {entry.Elapsed.TotalSeconds:F1}s）");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
            }

            // 点名要的都拿到了，剩下的没必要再下。
            // 只有真正解出文件的条目才算「拿到」：失败的留在 wanted 里，
            // 后面若还有同名条目可以再试，也避免了丢数据还谎报「已全部解出」。
            if (wanted is not null && entry.Error is null)
            {
                wanted.Remove(fileName);
                if (wanted.Count == 0)
                {
                    result.StoppedEarly = true;
                    onLog?.Invoke($"目标镜像已全部解出，停止下载（已下载 {result.DownloadedBytes / 1024.0 / 1024:F1} MB）");
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 只拉开头一小段（默认 8MB），解出 tar 里已经出现的条目名。
    /// gzip 只能从头解，但开头这几 MB 就够列出大部分镜像，让用户先挑。
    /// </summary>
    public static async Task<List<(string Name, long Size)>> PreviewAsync(
        HttpClient client, string url, long probeBytes, CancellationToken cancellationToken)
    {
        var entries = new List<(string, long)>();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, probeBytes - 1);

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return entries;

        await using Stream raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // 只拿到 gzip 的前几 MB，流必然是不完整的——解到截断处会抛，属于预期，直接收工返回已读到的。
        Stream tar;
        try
        {
            tar = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
        }
        catch
        {
            return entries;
        }

        byte[] header = new byte[512];
        try
        {
            while (entries.Count < 400)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadExactly(tar, header, 512))
                    break;
                if (header.All(b => b == 0))
                    break;

                string name = System.Text.Encoding.UTF8.GetString(header, 0, 100).Split((char)0)[0];
                string prefix = System.Text.Encoding.UTF8.GetString(header, 345, 155).Split((char)0)[0];
                if (prefix.Length > 0)
                    name = prefix + "/" + name;

                long size = ParseOctal(header, 124, 12);
                name = name.Replace((char)92, '/');

                bool isFile = !name.EndsWith("/") && size >= 0;
                if (isFile && (name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                               || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                    entries.Add((name, size));

                long skip = (size + 511) / 512 * 512;
                Skip(tar, skip);
            }
        }
        catch (InvalidDataException)
        {
            // gzip 片段被截断，属于正常结束
        }
        catch (EndOfStreamException)
        {
        }

        return entries;
    }

    private static bool TryReadExactly(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static void Skip(Stream stream, long count)
    {
        byte[] buffer = new byte[64 * 1024];
        while (count > 0)
        {
            int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) return;
            count -= n;
        }
    }

    private static long ParseOctal(byte[] buffer, int offset, int length)
    {
        long value = 0;
        for (int i = offset; i < offset + length; i++)
        {
            byte b = buffer[i];
            if (b == 0 || b == ' ') continue;
            if (b < '0' || b > '7') break;
            value = value * 8 + (b - '0');
        }
        return value;
    }

    /// <summary>统计已下载字节数用于进度显示。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _total;
        private readonly Result _result;
        private readonly Action<string, long, long>? _onProgress;
        private readonly CancellationToken _cancellationToken;
        private long _lastReportTicks;

        public CountingStream(Stream inner, long total, Result result, Action<string, long, long>? onProgress,
            CancellationToken cancellationToken)
        {
            _inner = inner;
            _total = total;
            _result = result;
            _onProgress = onProgress;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;

        public override long Position
        {
            get => _result.DownloadedBytes;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // 每次读都检查取消：否则解一个大 img 时点取消要等它读完才有反应
            _cancellationToken.ThrowIfCancellationRequested();
            int n = _inner.Read(buffer, offset, count);
            Report(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int n = _inner.Read(buffer);
            Report(n);
            return n;
        }

        private void Report(int n)
        {
            if (n <= 0)
                return;

            _result.DownloadedBytes += n;

            long now = Environment.TickCount64;
            if (now - _lastReportTicks < 100)
                return;

            _lastReportTicks = now;
            _onProgress?.Invoke("下载中", _result.DownloadedBytes, _total);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
