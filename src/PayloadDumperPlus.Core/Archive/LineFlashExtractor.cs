using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using PayloadDumperPlus.Core.Super;

namespace PayloadDumperPlus.Core.Archive;

public sealed class LineFlashEntry
{
    public required string EntryName { get; init; }
    public long Size { get; init; }
    public string OutputName { get; set; } = string.Empty;
    public bool Extracted { get; set; }
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public sealed class LineFlashOptions
{
    /// <summary>解出来后保留归档内的目录层级（images/xxx.img）。</summary>
    public bool KeepDirectoryStructure { get; init; }

    /// <summary>把 super.img 里的逻辑分区拆成独立 img。</summary>
    public bool UnpackSuper { get; init; } = true;

    /// <summary>拆完是否保留 super.img 原文件。</summary>
    public bool KeepSuper { get; init; } = true;

    /// <summary>对 sparse 格式的 img 自动展平成 raw。</summary>
    public bool ExpandSparse { get; init; } = true;
}

public sealed class LineFlashReport
{
    public required IReadOnlyList<LineFlashEntry> Images { get; init; }
    public IReadOnlyList<string> SuperPartitions { get; init; } = Array.Empty<string>();
    public TimeSpan Elapsed { get; init; }
    public long TotalBytes { get; init; }
    public int FailedCount => Images.Count(i => i.Error is not null);
}

/// <summary>
/// 线刷包（fastboot ROM）提取：tar / tar.gz / zip 里的镜像抽出来，
/// 顺带把 super.img 拆成各逻辑分区、把 sparse 镜像展平成 raw。
/// </summary>
public static class LineFlashExtractor
{
    private static readonly string[] ImageSuffixes = { ".img", ".img.br", ".bin" };

    public static LineFlashReport Extract(
        string packagePath,
        string outputDirectory,
        LineFlashOptions options,
        Action<string, long, long>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var images = new List<LineFlashEntry>();
        long totalBytes = 0;

        using (IArchive archive = ArchiveFactory.OpenArchive(packagePath, new ReaderOptions()))
        {
            foreach (IArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                    continue;

                string name = (entry.Key ?? string.Empty).Replace('\\', '/');
                if (!IsImageEntry(name))
                    continue;

                string outputName = BuildOutputName(name, options, outputDirectory);
                var record = new LineFlashEntry
                {
                    EntryName = name,
                    Size = entry.Size,
                    OutputName = outputName,
                };
                images.Add(record);

                try
                {
                    string fullPath = Path.Combine(outputDirectory, outputName);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                    using Stream source = entry.OpenEntryStream();
                    using (var target = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        long written = 0;
                        byte[] buffer = new byte[1 << 20];
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            target.Write(buffer, 0, read);
                            written += read;
                            onProgress?.Invoke(name, written, entry.Size);
                        }
                        totalBytes += written;
                    }

                    record.Extracted = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    record.Error = ex.Message;
                }
            }
        }

        var superPartitions = new List<string>();

        if (options.UnpackSuper)
        {
            string? superPath = images
                .Where(i => i.Extracted && Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(i.OutputName))
                    .Equals("super", StringComparison.OrdinalIgnoreCase))
                .Select(i => Path.Combine(outputDirectory, i.OutputName))
                .FirstOrDefault();

            if (superPath is not null && File.Exists(superPath))
            {
                try
                {
                    superPartitions.AddRange(UnpackSuper(superPath, outputDirectory, options, onProgress, cancellationToken));
                    if (!options.KeepSuper)
                        File.Delete(superPath);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // super 解析失败不影响其他镜像的产出
                    onProgress?.Invoke($"super 拆分失败：{ex.Message}", 0, 0);
                }
            }
        }

        if (options.ExpandSparse)
            ExpandSparseImages(images, outputDirectory, onProgress, cancellationToken);

        clock.Stop();
        return new LineFlashReport
        {
            Images = images,
            SuperPartitions = superPartitions,
            Elapsed = clock.Elapsed,
            TotalBytes = totalBytes,
        };
    }

    private static bool IsImageEntry(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.StartsWith("meta-inf/") || lower.Contains("/meta-inf/"))
            return false;
        return ImageSuffixes.Any(s => lower.EndsWith(s));
    }

    private static string BuildOutputName(string entryName, LineFlashOptions options, string outputDirectory)
    {
        string name = entryName.TrimStart('/');
        if (!options.KeepDirectoryStructure)
            name = Path.GetFileName(name);

        string candidate = name;
        int suffix = 1;
        while (File.Exists(Path.Combine(outputDirectory, candidate)))
        {
            string dir = Path.GetDirectoryName(name) ?? string.Empty;
            string file = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            string newName = $"{file}_{suffix++}{ext}";
            candidate = dir.Length > 0 ? Path.Combine(dir, newName) : newName;
        }

        return candidate;
    }

    private static List<string> UnpackSuper(string superPath, string outputDirectory, LineFlashOptions options,
        Action<string, long, long>? onProgress, CancellationToken cancellationToken)
    {
        var produced = new List<string>();

        using SuperImage super = SuperImage.Open(superPath);
        foreach (LpPartition partition in super.Partitions)
        {
            string fileName = partition.Name + ".img";
            string fullPath = Path.Combine(outputDirectory, fileName);

            // 与归档内已有的同名镜像冲突时加前缀，避免覆盖
            if (File.Exists(fullPath))
                fileName = "super_" + fileName;
            fullPath = Path.Combine(outputDirectory, fileName);

            super.ExtractPartition(partition, fullPath,
                (done, total) => onProgress?.Invoke($"super:{partition.Name}", done, total),
                cancellationToken);

            produced.Add(fileName);
        }

        return produced;
    }

    private static void ExpandSparseImages(List<LineFlashEntry> images, string outputDirectory,
        Action<string, long, long>? onProgress, CancellationToken cancellationToken)
    {
        foreach (LineFlashEntry image in images)
        {
            if (!image.Extracted)
                continue;

            string path = Path.Combine(outputDirectory, image.OutputName);
            if (!File.Exists(path) || !Sparse.SparseImage.IsSparse(path))
                continue;

            string temp = path + ".raw";
            try
            {
                using var sparse = Sparse.SparseImage.Open(path);
                sparse.ExpandTo(temp, (done, total) => onProgress?.Invoke("sparse:" + image.OutputName, done, total),
                    cancellationToken);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                if (File.Exists(temp))
                    File.Delete(temp);
                throw;
            }
        }
    }
}
