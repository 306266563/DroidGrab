using System.Buffers.Binary;
using System.IO.Compression;
using PayloadDumperPlus.Core.Sparse;

namespace PayloadDumperPlus.Core.Archive;

public enum PackageKind
{
    PayloadBin,
    OtaZip,
    LineFlashArchive,
    SparseImage,
    RawImage,
    Unknown,
}

/// <summary>payload 在磁盘上的位置。Offset 非零表示 payload.bin 内嵌在 zip 中间且未压缩。</summary>
public sealed record PayloadLocation(string FilePath, long Offset, bool IsTemporary, PackageKind Kind, string Description)
{
    public void Cleanup()
    {
        if (IsTemporary && File.Exists(FilePath))
        {
            try { File.Delete(FilePath); }
            catch { /* 临时文件清理失败不影响结果 */ }
        }
    }
}

/// <summary>按文件内容（magic）判断包类型，不靠后缀猜。</summary>
public static class PackageInspector
{
    public const uint GzipMagic = 0x00008B1F;
    public const uint ZipLocalMagic = 0x04034B50;

    public static PackageKind Detect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[512];
        int read = stream.Read(header);
        if (read < 4)
            return PackageKind.Unknown;

        header = header[..read];

        if (header.Length >= 4 && header[..4].SequenceEqual("CrAU"u8))
            return PackageKind.PayloadBin;

        if (SparseImage.IsSparse(header))
            return PackageKind.SparseImage;

        uint littleEndian = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (littleEndian == ZipLocalMagic || littleEndian == 0x06054B50 || littleEndian == 0x08074B50)
            return InspectZip(path);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) == GzipMagic)
            return PackageKind.LineFlashArchive;

        if (header.Length >= 262 && header.Slice(257, 5).SequenceEqual("ustar"u8))
            return PackageKind.LineFlashArchive;

        return PackageKind.Unknown;
    }

    private static PackageKind InspectZip(string path)
    {
        try
        {
            using ZipDirectory directory = ZipDirectory.Open(path);
            if (directory.FindPayload() is not null)
                return PackageKind.OtaZip;

            bool hasImages = directory.Entries.Any(e =>
                e.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ||
                e.Name.StartsWith("images/", StringComparison.OrdinalIgnoreCase));

            return hasImages ? PackageKind.LineFlashArchive : PackageKind.Unknown;
        }
        catch (InvalidDataException)
        {
            return PackageKind.Unknown;
        }
    }

    /// <summary>
    /// 定位 payload：
    ///  - 裸 payload.bin 直接返回原文件
    ///  - OTA zip 且 payload.bin 是 stored（未压缩）→ 返回 zip 路径 + entry 偏移，0 拷贝
    ///  - payload.bin 被压缩 → 解压到临时文件
    /// </summary>
    public static PayloadLocation ResolvePayload(string packagePath)
    {
        PackageKind kind = Detect(packagePath);

        if (kind == PackageKind.PayloadBin)
            return new PayloadLocation(packagePath, 0, false, kind, "裸 payload.bin");

        if (kind != PackageKind.OtaZip)
            throw new InvalidDataException($"不是卡刷包：识别为 {kind}");

        using ZipDirectory directory = ZipDirectory.Open(packagePath);
        ZipEntryInfo payload = directory.FindPayload()
                               ?? throw new InvalidDataException("OTA zip 里找不到 payload.bin");

        if (payload.IsStored)
        {
            return new PayloadLocation(packagePath, payload.DataOffset, false, kind,
                $"zip 内 payload.bin（stored，偏移 {payload.DataOffset:N0}，免解压）");
        }

        string temp = Path.Combine(Path.GetTempPath(), $"pdp-{Guid.NewGuid():N}",
            Path.GetFileName(payload.Name));
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);

        using Stream source = OpenEntryStream(packagePath, payload);
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            source.CopyTo(output);

        return new PayloadLocation(temp, 0, true, kind,
            $"zip 内 payload.bin（{DescribeMethod(payload.Method)}，已解压到临时文件）");
    }

    private static Stream OpenEntryStream(string zipPath, ZipEntryInfo target)
    {
        var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        // 不能用 using：ZipArchive 的所有权交给返回的 EntryStream（它在 Dispose 里
        // 先关解压流再关 ZipArchive）。加了 using 会在方法返回前就把底层流关掉，
        // 调用方拿到的流一个字也读不出来。
        var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry? entry = zip.GetEntry(target.Name);
        if (entry is null)
        {
            zip.Dispose();
            throw new InvalidDataException($"zip 里找不到条目 {target.Name}");
        }

        return new EntryStream(zip, entry.Open());
    }

    private static string DescribeMethod(ushort method) => method switch
    {
        0 => "stored",
        8 => "deflate",
        12 => "bzip2",
        14 => "lzma",
        93 => "zstd",
        _ => $"method {method}",
    };

    /// <summary>把"解压流"和它依赖的 ZipArchive 绑在一起，Dispose 时一起释放。</summary>
    private sealed class EntryStream : Stream
    {
        private readonly ZipArchive _owner;
        private readonly Stream _inner;

        public EntryStream(ZipArchive owner, Stream inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
