using System.Text;
using PayloadDumperPlus.Core.Proto;

namespace PayloadDumperPlus.Core.Payload;

/// <summary>InstallOperation 的操作类型，取值与 update_engine/update_metadata.proto 对齐。</summary>
public enum InstallOpType
{
    Replace = 0,
    ReplaceBz = 1,
    Move = 2,
    Bsdiff = 3,
    SourceCopy = 4,
    SourceBsdiff = 5,
    Zero = 6,
    Discard = 7,
    ReplaceXz = 8,
    Puffdiff = 9,
    BrotliBsdiff = 10,
    Zucchini = 11,
    Lz4diffBsdiff = 12,
    Lz4diffPuffdiff = 13,
}

public sealed class Extent
{
    public ulong StartBlock { get; init; }
    public ulong NumBlocks { get; init; }
}

public sealed class PartitionInfo
{
    public ulong Size { get; init; }
    public byte[]? Hash { get; init; }
}

public sealed class InstallOperation
{
    public InstallOpType Type { get; init; }
    public ulong DataOffset { get; init; }
    public ulong DataLength { get; init; }
    public IReadOnlyList<Extent> SrcExtents { get; init; } = Array.Empty<Extent>();
    public IReadOnlyList<Extent> DstExtents { get; init; } = Array.Empty<Extent>();
    public byte[]? DataSha256 { get; init; }
}

public sealed class PartitionUpdate
{
    public string PartitionName { get; init; } = string.Empty;
    public PartitionInfo? NewPartitionInfo { get; init; }
    public IReadOnlyList<InstallOperation> Operations { get; init; } = Array.Empty<InstallOperation>();

    /// <summary>该分区是否只能在拿到旧镜像时处理（增量包）。</summary>
    public bool RequiresSourceImage => Operations.Any(o => o.Type is
        InstallOpType.Move or InstallOpType.Bsdiff or InstallOpType.SourceCopy or
        InstallOpType.SourceBsdiff or InstallOpType.Puffdiff or InstallOpType.BrotliBsdiff or
        InstallOpType.Zucchini or InstallOpType.Lz4diffBsdiff or InstallOpType.Lz4diffPuffdiff);

    public long OutputSize => (long)(NewPartitionInfo?.Size ?? 0);
}

public sealed class DeltaArchiveManifest
{
    public uint BlockSize { get; init; } = 4096;
    public uint MinorVersion { get; init; }
    public bool PartialUpdate { get; init; }
    public IReadOnlyList<PartitionUpdate> Partitions { get; init; } = Array.Empty<PartitionUpdate>();

    public static DeltaArchiveManifest Parse(ReadOnlySpan<byte> manifest)
    {
        var builder = new ManifestBuilder();
        var reader = new WireReader(manifest);

        while (!reader.End)
        {
            (int field, int wire) = reader.ReadTag();
            switch (field)
            {
                case 3 when wire == 0:                       // block_size
                    builder.BlockSize = (uint)reader.ReadVarint();
                    break;
                case 12 when wire == 0:                      // minor_version
                    builder.MinorVersion = (uint)reader.ReadVarint();
                    break;
                case 13 when wire == 2:                      // partitions
                    builder.Partitions.Add(ParsePartition(reader.ReadLengthDelimited()));
                    break;
                case 16 when wire == 0:                      // partial_update
                    builder.PartialUpdate = reader.ReadVarint() != 0;
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        DeltaArchiveManifest result = builder.Build();
        Validate(result);
        return result;
    }

    private static PartitionUpdate ParsePartition(ReadOnlySpan<byte> data)
    {
        string name = string.Empty;
        PartitionInfo? newInfo = null;
        var operations = new List<InstallOperation>();
        var reader = new WireReader(data);

        while (!reader.End)
        {
            (int field, int wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:
                    name = reader.ReadString();
                    break;
                case 7 when wire == 2:
                    newInfo = ParsePartitionInfo(reader.ReadLengthDelimited());
                    break;
                case 8 when wire == 2:
                    operations.Add(ParseOperation(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new PartitionUpdate
        {
            PartitionName = name,
            NewPartitionInfo = newInfo,
            Operations = operations,
        };
    }

    private static PartitionInfo ParsePartitionInfo(ReadOnlySpan<byte> data)
    {
        ulong size = 0;
        byte[]? hash = null;
        var reader = new WireReader(data);

        while (!reader.End)
        {
            (int field, int wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 0:
                    size = reader.ReadVarint();
                    break;
                case 2 when wire == 2:
                    hash = reader.ReadLengthDelimited().ToArray();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new PartitionInfo { Size = size, Hash = hash };
    }

    private static InstallOperation ParseOperation(ReadOnlySpan<byte> data)
    {
        var type = InstallOpType.Replace;
        ulong dataOffset = 0, dataLength = 0;
        List<Extent>? srcExtents = null, dstExtents = null;
        byte[]? dataHash = null;
        var reader = new WireReader(data);

        while (!reader.End)
        {
            (int field, int wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 0:
                    type = (InstallOpType)(int)reader.ReadVarint();
                    break;
                case 2 when wire == 0:
                    dataOffset = reader.ReadVarint();
                    break;
                case 3 when wire == 0:
                    dataLength = reader.ReadVarint();
                    break;
                case 4 when wire == 2:
                    (srcExtents ??= new List<Extent>()).Add(ParseExtent(reader.ReadLengthDelimited()));
                    break;
                case 6 when wire == 2:
                    (dstExtents ??= new List<Extent>()).Add(ParseExtent(reader.ReadLengthDelimited()));
                    break;
                case 8 when wire == 2:
                    dataHash = reader.ReadLengthDelimited().ToArray();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new InstallOperation
        {
            Type = type,
            DataOffset = dataOffset,
            DataLength = dataLength,
            SrcExtents = srcExtents ?? (IReadOnlyList<Extent>)Array.Empty<Extent>(),
            DstExtents = dstExtents ?? (IReadOnlyList<Extent>)Array.Empty<Extent>(),
            DataSha256 = dataHash,
        };
    }

    private static Extent ParseExtent(ReadOnlySpan<byte> data)
    {
        ulong start = 0, count = 0;
        var reader = new WireReader(data);

        while (!reader.End)
        {
            (int field, int wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 0:
                    start = reader.ReadVarint();
                    break;
                case 2 when wire == 0:
                    count = reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new Extent { StartBlock = start, NumBlocks = count };
    }

    /// <summary>
    /// 字段号一旦写错，解析出来的分区名会是乱码、大小会离谱。
    /// 这里把这类"静默错位"变成显式报错。
    /// </summary>
    private static void Validate(DeltaArchiveManifest manifest)
    {
        if (manifest.BlockSize == 0 || (manifest.BlockSize & (manifest.BlockSize - 1)) != 0)
            throw new InvalidDataException($"manifest 异常：block_size = {manifest.BlockSize}（应为 2 的幂）");

        if (manifest.Partitions.Count == 0)
            throw new InvalidDataException("manifest 里没有任何分区，文件可能损坏或不是全量包");

        foreach (PartitionUpdate partition in manifest.Partitions)
        {
            if (partition.PartitionName.Length == 0)
                throw new InvalidDataException("manifest 异常：存在无名分区（字段号可能对不上）");

            foreach (char c in partition.PartitionName)
            {
                if (c < 0x21 || c > 0x7E)
                    throw new InvalidDataException(
                        $"manifest 异常：分区名含不可打印字符 \"{Describe(partition.PartitionName)}\"，解析可能错位");
            }

            if (partition.NewPartitionInfo is null || partition.OutputSize <= 0)
                throw new InvalidDataException($"manifest 异常：分区 {partition.PartitionName} 缺少有效大小");
        }
    }

    private static string Describe(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value.Take(24))
            sb.Append(c < 0x20 || c > 0x7E ? $"\\x{(int)c:X2}" : c.ToString());
        return sb.ToString();
    }

    private sealed class ManifestBuilder
    {
        public uint BlockSize { get; set; } = 4096;
        public uint MinorVersion { get; set; }
        public bool PartialUpdate { get; set; }
        public List<PartitionUpdate> Partitions { get; } = new();

        public DeltaArchiveManifest Build() => new()
        {
            BlockSize = BlockSize,
            MinorVersion = MinorVersion,
            PartialUpdate = PartialUpdate,
            Partitions = Partitions,
        };
    }
}
