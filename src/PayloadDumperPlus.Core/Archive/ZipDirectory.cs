using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PayloadDumperPlus.Core.Archive;

/// <summary>zip 中央目录里的一条记录（只保留本工具需要的字段）。</summary>
public sealed class ZipEntryInfo
{
    public required string Name { get; init; }
    public ushort Method { get; init; }
    public long CompressedSize { get; init; }
    public long UncompressedSize { get; init; }
    public long LocalHeaderOffset { get; init; }
    public long DataOffset { get; set; }

    public bool IsStored => Method == 0;
}

/// <summary>
/// 直接解析 zip 尾部结构，定位指定 entry 在文件里的真实偏移。
/// 目的：OTA zip 里的 payload.bin 通常是 stored，定位到偏移后可以跳过
/// "解压整个 zip 到临时文件"这一步，2GB+ 的包省一次完整拷贝。
/// </summary>
public sealed class ZipDirectory : IDisposable
{
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;
    private const uint LocalFileHeaderSignature = 0x04034b50;

    private readonly SafeFileHandle _handle;
    private readonly long _fileLength;

    public IReadOnlyList<ZipEntryInfo> Entries { get; }

    private ZipDirectory(SafeFileHandle handle, long fileLength, List<ZipEntryInfo> entries)
    {
        _handle = handle;
        _fileLength = fileLength;
        Entries = entries;
    }

    public static ZipDirectory Open(string path)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long length = RandomAccess.GetLength(handle);
            if (length < 22)
                throw new InvalidDataException("文件太小，不是有效 zip");

            // EOCD 在文件末尾，注释最长 65535，尾部多读一点更保险
            int tailSize = (int)Math.Min(length, 65535 + 22);
            byte[] tail = new byte[tailSize];
            RandomAccess.Read(handle, tail, length - tailSize);

            int eocdIndex = FindSignatureBackwards(tail, EocdSignature);
            if (eocdIndex < 0)
                throw new InvalidDataException("找不到 zip 的 EOCD 记录");

            long eocdAbsolute = length - tailSize + eocdIndex;
            long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocdIndex + 12, 4));
            long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocdIndex + 16, 4));
            ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocdIndex + 10, 2));

            if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
                ReadZip64End(handle, eocdAbsolute, ref cdOffset, ref cdSize);

            if (cdOffset < 0 || cdSize < 0 || cdOffset + cdSize > length)
                throw new InvalidDataException("zip 中央目录偏移越界");

            byte[] cd = new byte[cdSize];
            RandomAccess.Read(handle, cd, cdOffset);

            var entries = new List<ZipEntryInfo>(totalEntries);
            int position = 0;
            while (position + 46 <= cd.Length)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position)) != CentralFileHeaderSignature)
                    break;

                ushort method = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 10));
                long compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position + 20));
                long uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position + 24));
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 28));
                int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 30));
                int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(position + 32));
                long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(position + 42));

                string name = Encoding.UTF8.GetString(cd, position + 46, nameLength);

                if (localOffset == 0xFFFFFFFF || compressedSize == 0xFFFFFFFF || uncompressedSize == 0xFFFFFFFF)
                    ReadZip64Extra(cd.AsSpan(position + 46 + nameLength, extraLength),
                        ref uncompressedSize, ref compressedSize, ref localOffset);

                entries.Add(new ZipEntryInfo
                {
                    Name = name.Replace('\\', '/'),
                    Method = method,
                    CompressedSize = compressedSize,
                    UncompressedSize = uncompressedSize,
                    LocalHeaderOffset = localOffset,
                });

                position += 46 + nameLength + extraLength + commentLength;
            }

            foreach (ZipEntryInfo entry in entries)
                entry.DataOffset = ResolveDataOffset(handle, entry.LocalHeaderOffset);

            return new ZipDirectory(handle, length, entries);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ReadZip64End(SafeFileHandle handle, long eocdAbsolute, ref long cdOffset, ref long cdSize)
    {
        // Zip64 EOCD locator 紧挨在 EOCD 前 20 字节
        byte[] locator = new byte[20];
        long locatorOffset = eocdAbsolute - 20;
        if (locatorOffset < 0 || RandomAccess.Read(handle, locator, locatorOffset) != 20)
            throw new InvalidDataException("zip64：读不到 locator");

        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
            throw new InvalidDataException("zip64：locator 签名不匹配");

        long zip64EocdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));

        byte[] record = new byte[56];
        if (RandomAccess.Read(handle, record, zip64EocdOffset) != 56)
            throw new InvalidDataException("zip64：读不到 EOCD 记录");

        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EocdSignature)
            throw new InvalidDataException("zip64：EOCD 签名不匹配");

        cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
        cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
    }

    private static void ReadZip64Extra(ReadOnlySpan<byte> extra, ref long uncompressed, ref long compressed, ref long localOffset)
    {
        int position = 0;
        while (position + 4 <= extra.Length)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra[position..]);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(position + 2)..]);
            ReadOnlySpan<byte> body = extra.Slice(position + 4, Math.Min(size, extra.Length - position - 4));

            if (id == 0x0001)
            {
                int cursor = 0;
                if (uncompressed == 0xFFFFFFFF && cursor + 8 <= body.Length)
                {
                    uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[cursor..]);
                    cursor += 8;
                }
                if (compressed == 0xFFFFFFFF && cursor + 8 <= body.Length)
                {
                    compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[cursor..]);
                    cursor += 8;
                }
                if (localOffset == 0xFFFFFFFF && cursor + 8 <= body.Length)
                    localOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(body[cursor..]);
                return;
            }

            position += 4 + size;
        }
    }

    private static long ResolveDataOffset(SafeFileHandle handle, long localHeaderOffset)
    {
        byte[] header = new byte[30];
        if (RandomAccess.Read(handle, header, localHeaderOffset) != 30)
            throw new InvalidDataException("zip：local header 读取不完整");

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalFileHeaderSignature)
            throw new InvalidDataException($"zip：偏移 {localHeaderOffset} 处不是 local file header");

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        return localHeaderOffset + 30 + nameLength + extraLength;
    }

    private static int FindSignatureBackwards(byte[] buffer, uint signature)
    {
        for (int i = buffer.Length - 4; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) == signature)
                return i;
        }
        return -1;
    }

    public ZipEntryInfo? Find(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public ZipEntryInfo? FindPayload() =>
        Find("payload.bin") ?? Entries.FirstOrDefault(e =>
            e.Name.EndsWith("/payload.bin", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _handle.Dispose();
}
