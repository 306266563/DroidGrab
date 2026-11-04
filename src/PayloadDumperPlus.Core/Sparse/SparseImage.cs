using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PayloadDumperPlus.Core.Sparse;

/// <summary>Android sparse image 的只读访问层，对外暴露成"可按逻辑偏移读"的镜像。</summary>
public sealed class SparseImage : IDisposable, ILogicalImage
{
    public const uint SparseMagic = 0xED26FF3A;

    private const int FileHeaderSize = 28;
    private const int ChunkHeaderSize = 12;

    private const ushort ChunkRaw = 0xCAC1;
    private const ushort ChunkFill = 0xCAC2;
    private const ushort ChunkDontCare = 0xCAC3;
    private const ushort ChunkCrc32 = 0xCAC4;

    private readonly SafeFileHandle _handle;
    private readonly SparseChunk[] _chunks;

    public int BlockSize { get; }
    public long TotalBlocks { get; }
    public long Size => TotalBlocks * BlockSize;
    public string Path { get; }

    private SparseImage(string path, SafeFileHandle handle, int blockSize, long totalBlocks, SparseChunk[] chunks)
    {
        Path = path;
        _handle = handle;
        BlockSize = blockSize;
        TotalBlocks = totalBlocks;
        _chunks = chunks;
    }

    public static bool IsSparse(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == SparseMagic;

    public static bool IsSparse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4);
        Span<byte> header = stackalloc byte[4];
        return stream.Read(header) == 4 && IsSparse(header);
    }

    public static SparseImage Open(string path)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Span<byte> header = stackalloc byte[FileHeaderSize];
            if (RandomAccess.Read(handle, header, 0) != FileHeaderSize)
                throw new InvalidDataException("sparse 头部读取不完整");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic != SparseMagic)
                throw new InvalidDataException($"不是 sparse 镜像：magic 0x{magic:X8}");

            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            ushort fileHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            ushort chunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            uint totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            uint totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);

            if (major != 1)
                throw new InvalidDataException($"不支持的 sparse 主版本 {major}");
            if (fileHeaderSize < FileHeaderSize || chunkHeaderSize < ChunkHeaderSize)
                throw new InvalidDataException("sparse 头部长度的声明值异常");
            if (blockSize == 0 || blockSize > 1 << 20)
                throw new InvalidDataException($"sparse 块大小异常：{blockSize}");

            var chunks = new SparseChunk[totalChunks];
            Span<byte> chunkHeader = stackalloc byte[ChunkHeaderSize];
            long offset = fileHeaderSize;
            long blockCursor = 0;

            for (int i = 0; i < totalChunks; i++)
            {
                if (RandomAccess.Read(handle, chunkHeader, offset) != ChunkHeaderSize)
                    throw new InvalidDataException($"第 {i} 个 chunk 头部读取失败");

                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(chunkHeader);
                uint chunkBlocks = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
                uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[8..]);

                if (totalSize < chunkHeaderSize)
                    throw new InvalidDataException($"第 {i} 个 chunk 总长度异常：{totalSize}");

                chunks[i] = new SparseChunk(type, offset + chunkHeaderSize, chunkBlocks, blockCursor);
                blockCursor += chunkBlocks;
                offset += totalSize;
            }

            return new SparseImage(path, handle, (int)blockSize, totalBlocks, chunks);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            long position = offset + total;
            if (position >= Size)
                break;

            long blockIndex = position / BlockSize;
            int offsetInBlock = (int)(position % BlockSize);
            int chunkIndex = FindChunk(blockIndex);

            if (chunkIndex < 0)
            {
                // 末尾超出 chunk 覆盖范围，按零填充
                int zeros = Math.Min(buffer.Length - total, BlockSize - offsetInBlock);
                buffer.Slice(total, zeros).Clear();
                total += zeros;
                continue;
            }

            SparseChunk chunk = _chunks[chunkIndex];
            int take = Math.Min(buffer.Length - total, BlockSize - offsetInBlock);

            switch (chunk.Type)
            {
                case ChunkDontCare:
                case ChunkCrc32:
                    buffer.Slice(total, take).Clear();
                    break;

                case ChunkFill:
                {
                    Span<byte> fill = stackalloc byte[4];
                    RandomAccess.Read(_handle, fill, chunk.DataOffset);
                    for (int i = 0; i < take; i++)
                        buffer[total + i] = fill[i & 3];
                    break;
                }

                case ChunkRaw:
                {
                    long sourceOffset = chunk.DataOffset + (blockIndex - chunk.StartBlock) * (long)BlockSize + offsetInBlock;
                    int read = 0;
                    while (read < take)
                    {
                        int n = RandomAccess.Read(_handle, buffer.Slice(total + read, take - read), sourceOffset + read);
                        if (n <= 0)
                            throw new EndOfStreamException("sparse raw chunk 数据被截断");
                        read += n;
                    }
                    break;
                }

                default:
                    throw new InvalidDataException($"未知的 sparse chunk 类型 0x{chunk.Type:X4}");
            }

            total += take;
        }

        return total;
    }

    private int FindChunk(long blockIndex)
    {
        int low = 0, high = _chunks.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            SparseChunk chunk = _chunks[mid];
            if (blockIndex < chunk.StartBlock)
                high = mid - 1;
            else if (blockIndex >= chunk.StartBlock + chunk.Blocks)
                low = mid + 1;
            else
                return mid;
        }
        return -1;
    }

    /// <summary>把 sparse 镜像展开成 raw 文件。</summary>
    public void ExpandTo(string outputPath, Action<long, long>? progress = null, CancellationToken cancellationToken = default)
    {
        const int bufferSize = 8 * 1024 * 1024;
        byte[] buffer = new byte[bufferSize];

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        output.SetLength(Size);

        long done = 0;
        while (done < Size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int want = (int)Math.Min(bufferSize, Size - done);
            int read = Read(done, buffer.AsSpan(0, want));
            if (read <= 0)
                break;
            output.Write(buffer, 0, read);
            done += read;
            progress?.Invoke(done, Size);
        }
    }

    public void Dispose() => _handle.Dispose();

    private readonly record struct SparseChunk(ushort Type, long DataOffset, long Blocks, long StartBlock);
}

/// <summary>可按逻辑偏移读取的镜像抽象，供 super 拆分统一使用。</summary>
public interface ILogicalImage : IDisposable
{
    long Size { get; }
    int Read(long offset, Span<byte> buffer);
}
