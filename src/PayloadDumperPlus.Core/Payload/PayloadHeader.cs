using System.Buffers.Binary;
using System.Text;

namespace PayloadDumperPlus.Core.Payload;

/// <summary>
/// Android OTA payload.bin 的文件头。
/// 结构（全部大端）：
///   magic[4] = "CrAU"
///   uint64 format_version
///   uint64 manifest_size
///   uint32 metadata_signature_size   // 仅 format_version >= 2
/// 随后是 manifest，再是 metadata 签名，最后是数据 blob 区。
/// manifest 里的 data_offset 是相对「数据 blob 区起始」的偏移，不是文件偏移。
/// </summary>
public sealed class PayloadHeader
{
    public static ReadOnlySpan<byte> Magic => "CrAU"u8;

    public uint FormatVersion { get; private init; }
    public ulong ManifestSize { get; private init; }
    public uint MetadataSignatureSize { get; private init; }
    public int HeaderSize { get; private init; }

    /// <summary>数据 blob 区在文件中的绝对起始偏移。</summary>
    public long DataOffset { get; private init; }

    public static PayloadHeader Read(Stream stream)
    {
        Span<byte> head = stackalloc byte[24];
        stream.ReadExactly(head[..20]);

        if (!head[..4].SequenceEqual(Magic))
        {
            uint actual = BinaryPrimitives.ReadUInt32BigEndian(head[..4]);
            throw new InvalidDataException(
                $"不是 payload.bin：magic 0x{actual:X8}，期望 \"CrAU\"");
        }

        uint version = (uint)BinaryPrimitives.ReadUInt64BigEndian(head.Slice(4, 8));
        ulong manifestSize = BinaryPrimitives.ReadUInt64BigEndian(head.Slice(12, 8));

        if (version is 0 or > 2)
            throw new InvalidDataException($"不支持的 payload 格式版本 {version}");

        int headerSize = 20;
        uint signatureSize = 0;

        if (version >= 2)
        {
            stream.ReadExactly(head[..4]);
            signatureSize = BinaryPrimitives.ReadUInt32BigEndian(head[..4]);
            headerSize = 24;
        }

        if (manifestSize == 0 || manifestSize > 64UL * 1024 * 1024)
            throw new InvalidDataException($"manifest 大小异常：{manifestSize} 字节");

        long dataOffset = headerSize + (long)manifestSize + signatureSize;

        return new PayloadHeader
        {
            FormatVersion = version,
            ManifestSize = manifestSize,
            MetadataSignatureSize = signatureSize,
            HeaderSize = headerSize,
            DataOffset = dataOffset,
        };
    }


    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append($"格式版本 v{FormatVersion}，manifest {ManifestSize / 1024.0:F1} KB");
        if (MetadataSignatureSize > 0)
            sb.Append($"，metadata 签名 {MetadataSignatureSize} 字节");
        return sb.ToString();
    }
}
