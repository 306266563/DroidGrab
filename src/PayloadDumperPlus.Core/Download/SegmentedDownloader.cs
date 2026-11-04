using System.Net;
using System.Net.Http.Headers;

namespace PayloadDumperPlus.Core.Download;

public enum ProxyMode
{
    /// <summary>跟随系统代理设置（默认）。</summary>
    System,

    /// <summary>忽略所有代理，直接连接。</summary>
    Direct,

    /// <summary>使用 <see cref="DownloadOptions.Proxy"/> 指定的代理。</summary>
    Custom,
}

/// <summary>
/// 远程访问的并发与网络设置。真实并发由 <c>HttpRangeReader</c> 的闸门执行：
/// 它按这里的 <see cref="MaxConnections"/> / <see cref="LiveConnections"/> 限制同时在飞的 Range 请求数，
/// 并按吞吐自动升降档。
/// </summary>
public sealed class DownloadOptions
{
    /// <summary>并发 Range 请求数上限（1~32），由界面滑块决定；实际会按吞吐自动升降档。</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>运行期间读取当前允许的并发数；为空时沿用 MaxConnections。用于拖动滑块即时生效。</summary>
    public Func<int>? LiveConnections { get; init; }

    /// <summary>自动升降档把档位改掉时回调，参数是新的档位。用来让界面上的滑块跟着动。</summary>
    public Action<int>? OnConcurrencyAdjusted { get; init; }

    /// <summary>是否允许按吞吐自动升降档；关掉后只按用户设定跑。</summary>
    public bool AutoDownshift { get; init; } = true;

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public string? Referer { get; init; }
    public string? Cookie { get; init; }
    public string? Proxy { get; init; }
    public ProxyMode ProxyMode { get; init; } = ProxyMode.System;
}

public sealed class DownloadProbe
{
    public required string FinalUrl { get; init; }
    public long TotalBytes { get; init; }
    public bool SupportsRange { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public string? SuggestedFileName { get; init; }
}

/// <summary>
/// 远程包的探测器和 HTTP 客户端持有者。
/// 负责一次 Range(0,0) 探测拿到总大小 / 是否支持 Range / 最终文件名，
/// 并按 <see cref="DownloadOptions"/> 的代理设置构造 HttpClient。
/// 数据读取由 <c>HttpRangeReader</c> 完成，它复用同一个 HttpClient。
/// </summary>
public sealed class SegmentedDownloader : IDisposable
{
    private readonly HttpClient _client;
    private readonly DownloadOptions _options;

    public SegmentedDownloader(DownloadOptions options)
    {
        _options = options;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = Math.Clamp(options.MaxConnections, 1, 32) * 2,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        switch (options.ProxyMode)
        {
            case ProxyMode.Direct:
                handler.UseProxy = false;
                break;

            case ProxyMode.Custom when !string.IsNullOrWhiteSpace(options.Proxy):
                handler.Proxy = new WebProxy(options.Proxy);
                handler.UseProxy = true;
                break;

            default:
                handler.UseProxy = true;
                break;
        }

        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            // 部分 CDN（小米 ultimateota 等）在 HTTP/2 下会对 Range 请求只回一小段然后挂住，
            // 表现为「下到几十 KB 就不动了」。强制走 HTTP/1.1 是稳的。
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
    }

    public async Task<DownloadProbe> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        // 不发 HEAD：小米这类 CDN 的 HEAD 要额外一次 TLS 往返，慢好几秒。
        // 直接要 1 个字节，Content-Range 里就带总大小和支持情况，一次搞定。
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        // 有些源收了请求就不回响应头，没有超时就是无限干等（界面上表现为卡在"探测链接"）
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(TimeSpan.FromSeconds(20));

        HttpResponseMessage response;
        try
        {
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                "探测超时：20 秒内服务器没有响应。链接可能已失效、需要 Referer/Cookie，或当前网络访问不了这个域名。");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                throw new HttpRequestException(
                    response.StatusCode == HttpStatusCode.Forbidden
                        ? "服务器拒绝访问（403）：链接可能需要登录，或 UA/Referer/Cookie 不对"
                        : "文件不存在（404）：链接可能已失效");
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"探测失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            // 这里必须优先读 Content-Range：我们只要了 1 个字节，ContentLength 就是 1，
            // 真实总大小在 `bytes 0-0/9730785280` 的斜杠后面。
            long total = -1;
            string? contentRange = response.Content.Headers.ContentRange?.ToString();
            if (contentRange is not null)
            {
                int slash = contentRange.LastIndexOf('/');
                if (slash >= 0 && long.TryParse(contentRange[(slash + 1)..], out long parsed) && parsed > 0)
                    total = parsed;
            }

            if (total <= 0)
                total = response.Content.Headers.ContentLength ?? -1;

            if (total <= 0)
                throw new HttpRequestException("服务器没有返回文件大小，无法分段下载");

            bool supportsRange = response.StatusCode == HttpStatusCode.PartialContent ||
                                 response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);

            string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            string? etag = response.Headers.ETag?.Tag;
            string? lastModified = response.Content.Headers.LastModified?.ToString("R");
            string? fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                               ?? response.Content.Headers.ContentDisposition?.FileName;

            return new DownloadProbe
            {
                FinalUrl = finalUrl,
                TotalBytes = total,
                SupportsRange = supportsRange,
                ETag = etag,
                LastModified = lastModified,
                SuggestedFileName = fileName?.Trim('"'),
            };
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.Referer))
            request.Headers.Referrer = new Uri(_options.Referer);
        if (!string.IsNullOrWhiteSpace(_options.Cookie))
            request.Headers.TryAddWithoutValidation("Cookie", _options.Cookie);
    }

    public void Dispose() => _client.Dispose();
}
