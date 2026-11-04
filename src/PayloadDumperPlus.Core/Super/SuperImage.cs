using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using PayloadDumperPlus.Core.Sparse;

namespace PayloadDumperPlus.Core.Super;

public readonly record struct LpExtent(long NumSectors, uint TargetType, long TargetData, uint TargetSource)
{
    public const uint Linear = 0;
    public const uint Zero = 1;

    public long SizeBytes => NumSectors * 512;
}

public sealed class LpPartition
{
    public required string Name { get; init; }
    public uint Attributes { get; init; }
    public uint GroupIndex { get; init; }
    public IReadOnlyList<LpExtent> Extents { get; init; } = Array.Empty<LpExtent>();

    public long SizeBytes => Extents.Sum(e => e.SizeBytes);

    /// <summary>去掉 A/B 槽位后缀，得到"逻辑名字"（system_a → system）。</summary>
    public string BaseName
    {
        get
        {
            if (Name.Length > 2 && (Name.EndsWith("_a") || Name.EndsWith("_b")))
                return Name[..^2];
            return Name;
        }
    }
}

/// <summary>
/// super.img（dynamic partitions 容器）解析与拆分。
///
/// 两个实测要点（原生 lpunpack 和厂商布局差异都会踩）：
///  1. extent 的 target_data 单位是 **512 字节扇区**，不是字节；
///  2. 元数据头（0x414C5030）的位置在各厂商镜像里不一致，套 liblp 的通用公式
///     会算到全零区域，必须扫描定位。
/// </summary>
public sealed class SuperImage : IDisposable
{
    private const uint GeometryMagic = 0x616C4467;
    private const uint HeaderMagic = 0x414C5030;
    private const int GeometryOffset = 4096;
    private const int MetadataScanSize = 32 * 1024 * 1024;

    private readonly ILogicalImage _image;

    public IReadOnlyList<LpPartition> Partitions { get; }
    public long MetadataOffset { get; }
    public uint LogicalBlockSize { get; }
    public long Size => _image.Size;
    public bool IsSparse { get; }

    private SuperImage(ILogicalImage image, bool isSparse, IReadOnlyList<LpPartition> partitions,
        long metadataOffset, uint logicalBlockSize)
    {
        _image = image;
        IsSparse = isSparse;
        Partitions = partitions;
        MetadataOffset = metadataOffset;
        LogicalBlockSize = logicalBlockSize;
    }


    public static SuperImage Open(string path)
    {
        bool isSparse = SparseImage.IsSparse(path);
        ILogicalImage image = OpenLogical(path);
        try
        {
            if (!TryReadGeometry(image, out uint maxSize, out uint slotCount, out uint logicalBlockSize))
                throw new InvalidDataException("不是 super 镜像：偏移 4096 处没有 LP geometry 魔数");

            byte[] scan = new byte[(int)Math.Min(MetadataScanSize, image.Size)];
            image.Read(0, scan);

            int headerIndex = FindHeader(scan);
            if (headerIndex < 0)
                throw new InvalidDataException("扫描不到 LP 元数据头（0x414C5030）");

            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(scan.AsSpan(headerIndex + 8));
            uint tablesSize = BinaryPrimitives.ReadUInt32LittleEndian(scan.AsSpan(headerIndex + 44));

            if (headerSize < 128 || headerSize > 4096)
                throw new InvalidDataException($"LP 头长度异常：{headerSize}");
            if (tablesSize == 0 || tablesSize > 16 * 1024 * 1024)
                throw new InvalidDataException($"LP 表长度异常：{tablesSize}");

            var partitionsDesc = ReadDescriptor(scan, headerIndex + 80);
            var extentsDesc = ReadDescriptor(scan, headerIndex + 92);

            byte[] tables = new byte[tablesSize];
            image.Read(headerIndex + headerSize, tables);

            List<LpExtent> extents = ReadExtents(tables, extentsDesc);
            List<LpPartition> partitions = ReadPartitions(tables, partitionsDesc, extents);

            _ = maxSize;
            _ = slotCount;

            return new SuperImage(image, isSparse, partitions, headerIndex, logicalBlockSize);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static ILogicalImage OpenLogical(string path) =>
        SparseImage.IsSparse(path) ? SparseImage.Open(path) : new RawImage(path);

    private static bool TryReadGeometry(ILogicalImage image, out uint maxSize, out uint slotCount, out uint logicalBlockSize)
    {
        maxSize = slotCount = logicalBlockSize = 0;
        if (image.Size < GeometryOffset + 52)
            return false;

        byte[] geometry = new byte[52];
        image.Read(GeometryOffset, geometry);

        if (BinaryPrimitives.ReadUInt32LittleEndian(geometry) != GeometryMagic)
            return false;

        maxSize = BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(40));
        slotCount = BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(44));
        logicalBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(48));
        return true;
    }

    private static int FindHeader(ReadOnlySpan<byte> buffer)
    {
        Span<byte> magic = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, HeaderMagic);
        return buffer.IndexOf(magic);
    }

    private static (uint Offset, uint Count, uint EntrySize) ReadDescriptor(ReadOnlySpan<byte> header, int offset) => (
        BinaryPrimitives.ReadUInt32LittleEndian(header[offset..]),
        BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 4)..]),
        BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 8)..]));

    private static List<LpExtent> ReadExtents(byte[] tables, (uint Offset, uint Count, uint EntrySize) descriptor)
    {
        var list = new List<LpExtent>((int)descriptor.Count);
        for (int i = 0; i < descriptor.Count; i++)
        {
            int offset = checked((int)(descriptor.Offset + i * descriptor.EntrySize));
            if (offset + 24 > tables.Length)
                break;

            long numSectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(offset));
            uint targetType = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(offset + 8));
            long targetData = (long)BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(offset + 12));
            uint targetSource = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(offset + 20));
            list.Add(new LpExtent(numSectors, targetType, targetData, targetSource));
        }
        return list;
    }

    private static List<LpPartition> ReadPartitions(byte[] tables,
        (uint Offset, uint Count, uint EntrySize) descriptor, List<LpExtent> extents)
    {
        var list = new List<LpPartition>((int)descriptor.Count);

        for (int i = 0; i < descriptor.Count; i++)
        {
            int offset = checked((int)(descriptor.Offset + i * descriptor.EntrySize));
            if (offset + 52 > tables.Length)
                break;

            string name = System.Text.Encoding.ASCII
                .GetString(tables, offset, 36)
                .Split('\0')[0];

            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(offset + 36));
            uint firstExtent = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(offset + 40));
            uint numExtents = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(offset + 44));
            uint groupIndex = BinaryPrimitives.ReadUInt32LittleEndian(tables.AsSpan(offset + 48));

            var own = new List<LpExtent>((int)numExtents);
            for (int k = 0; k < numExtents; k++)
            {
                int index = (int)firstExtent + k;
                if (index >= 0 && index < extents.Count)
                    own.Add(extents[index]);
            }

            if (name.Length == 0)
                continue;

            list.Add(new LpPartition
            {
                Name = name,
                Attributes = attributes,
                GroupIndex = groupIndex,
                Extents = own,
            });
        }

        return list;
    }

    /// <summary>把某个逻辑分区写成独立镜像文件。</summary>
    public void ExtractPartition(LpPartition partition, string outputPath,
        Action<long, long>? progress = null, CancellationToken cancellationToken = default)
    {
        const int chunk = 8 * 1024 * 1024;
        byte[] buffer = new byte[chunk];

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        output.SetLength(partition.SizeBytes);

        long writtenTotal = 0;
        foreach (LpExtent extent in partition.Extents)
        {
            long remaining = extent.SizeBytes;
            long source = extent.TargetData * 512;

            if (extent.TargetType != LpExtent.Linear)
            {
                // ZERO / 其他非线性的 extent 直接留空（文件预分配即全零）
                writtenTotal += extent.SizeBytes;
                progress?.Invoke(writtenTotal, partition.SizeBytes);
                continue;
            }

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int take = (int)Math.Min(chunk, remaining);
                int read = _image.Read(source, buffer.AsSpan(0, take));
                if (read <= 0)
                {
                    Array.Clear(buffer, 0, take);
                    read = take;
                }

                output.Write(buffer, 0, read);
                source += read;
                remaining -= read;
                writtenTotal += read;
                progress?.Invoke(writtenTotal, partition.SizeBytes);
            }
        }
    }

    public void Dispose() => _image.Dispose();

    private sealed class RawImage : ILogicalImage
    {
        private readonly SafeFileHandle _handle;

        public RawImage(string path)
        {
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Size = RandomAccess.GetLength(_handle);
        }

        public long Size { get; }

        public int Read(long offset, Span<byte> buffer)
        {
            if (offset >= Size)
                return 0;
            int want = (int)Math.Min(buffer.Length, Size - offset);
            return RandomAccess.Read(_handle, buffer[..want], offset);
        }

        public void Dispose() => _handle.Dispose();
    }
}
