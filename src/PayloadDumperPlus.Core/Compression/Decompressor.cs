using System.IO.Compression;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace PayloadDumperPlus.Core.Compression;

public enum CompressionKind
{
    Xz,
    BZip2,
    Brotli,
}

/// <summary>
/// payload 内单个 operation 的解压封装。输入来自 ArrayPool，输出写入调用方提供的定长缓冲。
/// </summary>
public static class Decompressor
{
    public static int Decompress(CompressionKind kind, byte[] input, int offset, int count, Span<byte> output)
    {
        using var source = new MemoryStream(input, offset, count, writable: false);
        using Stream decoder = kind switch
        {
            CompressionKind.Xz => new XZStream(source),
            CompressionKind.BZip2 => BZip2Stream.Create(
                source, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false),
            CompressionKind.Brotli => new BrotliStream(source, CompressionMode.Decompress, false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知压缩类型"),
        };

        int total = 0;
        while (total < output.Length)
        {
            int read = decoder.Read(output[total..]);
            if (read <= 0)
                break;
            total += read;
        }

        return total;
    }
}
