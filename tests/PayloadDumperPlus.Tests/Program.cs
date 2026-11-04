using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PayloadDumperPlus.Core.Payload;
using PayloadDumperPlus.Core.Pipeline;

namespace PayloadDumperPlus.Tests;

internal static class Program
{
    private static int _failures;

    private sealed class ConsoleProgress : IProgress<PipelineEvent>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void Report(PipelineEvent e)
        {
            Console.WriteLine(
                $"[{_clock.Elapsed.TotalSeconds,7:F1}s] {e.Stage,-11} {e.Message,-34} " +
                $"{e.Fraction * 100,6:F1}%  {e.BytesDone,14:N0}/{e.BytesTotal,-14:N0} {e.BytesPerSecond / 1024.0 / 1024.0,6:F2} MB/s");
        }
    }

    private static async Task<int> RunRemoteDiag(string[] args)
    {
        string url = args[0];
        string outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "pdp_diag");
        string[]? parts = args.Length > 2 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : null;
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"URL: {url}\nOUT: {outDir}\nPARTS: {string.Join(",", parts ?? Array.Empty<string>())}\n");

        var options = new PipelineOptions
        {
            Source = url,
            OutputDirectory = outDir,
            PartitionSelector = _ => parts,
            OnlyImages = parts?.Select(x => x.Contains('.') ? x : x + ".img").ToArray(),
        };

        try
        {
            PipelineResult result = await ExtractPipeline.RunAsync(options, new ConsoleProgress());
            Console.WriteLine($"\n=== OK kind={result.Kind} elapsed={result.Elapsed.TotalSeconds:F1}s bytes={result.TotalBytes:N0}");
            foreach (PartitionResult p in result.Partitions)
                Console.WriteLine($"  {p.Name,-24} success={p.Success} skipped={p.Skipped} size={p.Size,14:N0} elapsed={p.Elapsed.TotalSeconds,6:F1}s {p.Error}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n=== FAIL {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && (args[0].StartsWith("http", StringComparison.OrdinalIgnoreCase) || File.Exists(args[0])))
            return await RunRemoteDiag(args);

        string? payloadPath = args.Length > 0 ? args[0] : LocateSample("sample_full.bin");
        if (payloadPath is null || !File.Exists(payloadPath))
        {
            Console.Error.WriteLine("找不到合成样本，先运行：python make_sample_payload.py");
            return 2;
        }

        string expectedPath = Path.Combine(Path.GetDirectoryName(payloadPath)!, "expected.json");
        string outputDir = Path.Combine(Path.GetDirectoryName(payloadPath)!, "extracted");

        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);

        Console.WriteLine($"样本：{payloadPath} ({new FileInfo(payloadPath).Length / 1024.0 / 1024.0:F2} MB)");

        using PayloadExtractor extractor = PayloadExtractor.Open(payloadPath);
        Console.WriteLine($"头：{extractor.Header.Describe()}");
        Console.WriteLine($"block_size={extractor.Manifest.BlockSize}，分区 {extractor.Manifest.Partitions.Count} 个");
        foreach (PartitionUpdate p in extractor.Manifest.Partitions)
        {
            Console.WriteLine($"  {p.PartitionName,-12} size={p.OutputSize,10:N0}  ops={p.Operations.Count,4}  " +
                              $"类型={string.Join("/", p.Operations.Select(o => o.Type).Distinct())}");
            if (p.OutputSize % extractor.Manifest.BlockSize != 0)
                Fail($"{p.PartitionName} 大小不是块整数倍");
        }

        var stopwatch = Stopwatch.StartNew();
        ExtractionReport report = await extractor.ExtractAsync(outputDir, new ExtractionOptions
        {
            MaxConcurrency = Math.Clamp(Environment.ProcessorCount, 1, 32),
            VerifyPartitionHash = true,
        }, new Progress<ProgressEvent>(e =>
        {
            if (e.Kind == ProgressKind.PartitionCompleted)
                Console.WriteLine($"  [完成] {e.Partition}");
            else if (e.Kind == ProgressKind.PartitionFailed)
                Console.WriteLine($"  [失败] {e.Partition}: {e.Message}");
        }));
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"提取完成：成功 {report.SuccessCount}，失败 {report.FailedCount}，跳过 {report.SkippedCount}，" +
                          $"耗时 {report.Elapsed.TotalSeconds:F2}s，" +
                          $"吞吐 {report.TotalBytes / 1024.0 / 1024.0 / Math.Max(0.001, report.Elapsed.TotalSeconds):F1} MB/s");

        if (report.FailedCount > 0)
            Fail("存在失败分区");

        // 与 Python 侧独立算出的 sha256 比对
        if (File.Exists(expectedPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(expectedPath));
            JsonElement partitions = doc.RootElement.GetProperty("partitions");
            foreach (JsonProperty entry in partitions.EnumerateObject())
            {
                string name = entry.Name;
                string expectedHash = entry.Value.GetProperty("sha256").GetString()!;
                long expectedSize = entry.Value.GetProperty("size").GetInt64();

                string path = Path.Combine(outputDir, name + ".img");
                if (!File.Exists(path))
                {
                    Fail($"{name} 没有产出文件");
                    continue;
                }

                long actualSize = new FileInfo(path).Length;
                if (actualSize != expectedSize)
                    Fail($"{name} 大小不符：{actualSize} != {expectedSize}");

                string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                if (actualHash != expectedHash)
                    Fail($"{name} sha256 不符：{actualHash} != {expectedHash}");
                else
                    Console.WriteLine($"  ✓ {name,-12} {expectedSize / 1024.0:F1} KB  sha256 一致");
            }
        }
        else
        {
            Console.WriteLine($"（没有 {expectedPath}，跳过逐分区比对）");
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "全部通过" : $"失败 {_failures} 项");
        return _failures == 0 ? 0 : 1;
    }

    private static string? LocateSample(string fileName)
    {
        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        // 从工作区递归找（跳过 bin/obj，避免扫到构建产物）
        DirectoryInfo root = new(Directory.GetCurrentDirectory());
        if (!root.Exists)
            return null;

        return root.EnumerateFiles(fileName, SearchOption.AllDirectories)
            .Where(f => f.Directory?.Name == "samples")
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    private static void Fail(string message)
    {
        _failures++;
        Console.WriteLine($"  ✗ {message}");
    }
}
