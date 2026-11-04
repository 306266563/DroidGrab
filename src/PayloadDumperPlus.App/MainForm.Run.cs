using System.Text;
using PayloadDumperPlus.Core.Download;
using PayloadDumperPlus.Core.Payload;
using PayloadDumperPlus.Core.Pipeline;

namespace PayloadDumperPlus.App;

public sealed partial class MainForm
{
    // ---------- 交互 ----------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            _source.Text = files[0];
    }

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 OTA 包 / payload.bin / 线刷包",
            Filter = "支持的包|*.bin;*.zip;*.tar;*.tgz;*.gz;*.img;*.br|" +
                     "payload.bin|payload.bin|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _source.Text = dialog.FileName;
    }

    /// <summary>下载并发被自动调档后，把滑块和数值同步过去，并在日志里说一声。</summary>
    private void OnDownloadTuned(int limit)
    {
        if (IsDisposed || Disposing)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed)
                    return;

                int clamped = Math.Clamp(limit, _dlThreads.Minimum, _dlThreads.Maximum);
                Interlocked.Exchange(ref _tuningFromCore, 1);
                try
                {
                    if (_dlThreads.Value != clamped)
                        _dlThreads.Value = clamped;
                    _dlValue.Text = clamped.ToString();
                }
                finally
                {
                    Interlocked.Exchange(ref _tuningFromCore, 0);
                }

                AppendLog($"[调档] 下载并发调整为 {clamped}（实测这个档位最快）");
            }));
        }
        catch (InvalidOperationException)
        {
            // 窗口已经在关了，忽略
        }
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        string source = _source.Text.Trim();
        if (source.Length == 0)
        {
            if (_autoStart)
                AppendLog("命令行没有给出包路径，已停在待机状态。");
            else
                MessageBox.Show(this, "先填一个包路径或者下载直链。", "还没输入", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            return;
        }

        if (!ExtractPipeline.LooksLikeUrl(source) && !File.Exists(source))
        {
            if (_autoStart)
                AppendLog($"找不到文件：{source}");
            else
                MessageBox.Show(this, $"找不到文件：\n{source}", "文件不存在", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            return;
        }

        SetBusy(true);
        Interlocked.Exchange(ref _lastProgressTicks, Environment.TickCount64);
        _progress.Running = true;
        _stallWatch.Start();
        _grid.Rows.Clear();
        ResetProgressPeaks();
        if (_forcedPartitions is { Count: > 0 })
            Volatile.Write(ref _partitionTotal, _forcedPartitions.Count);

        _progress.Value = 0;
        _status.Text = "准备中…";
        _detail.Text = string.Empty;
        _lastLoggedStage = PipelineStage.Idle;
        _loggedPartitions.Clear();
        _lastVerifyPct = -1;

        // --auto 是给无人值守用的，但没给 --parts 的时候仍然会弹分区选择框等人点，
        // 提前写一句日志，免得用户以为程序卡住了。
        if (_autoStart && _forcedPartitions is null)
            AppendLog("提示：没有指定 --parts，稍后会弹出分区选择框，请手动勾选。");

        _cts = new CancellationTokenSource();
        var options = new PipelineOptions
        {
            Source = source,
            OutputDirectory = string.IsNullOrWhiteSpace(_outputDir.Text) ? _preferredOutputDirectory : _outputDir.Text.Trim(),
            UserAgent = _settings.UserAgent,
            Referer = _settings.Referer,
            Cookie = _settings.Cookie,
            DownloadConnections = _dlThreads.Value,
            ExtractThreads = _exThreads.Value,
            VerifyHash = _verify.Checked,
            UnpackSuper = _unpackSuper.Checked,
            KeepSuper = _keepSuper.Checked,
            ProxyMode = !string.IsNullOrWhiteSpace(_proxyBox.Text)
                ? ProxyMode.Custom
                : _directProxy.Checked ? ProxyMode.Direct : ProxyMode.System,
            Proxy = _proxyBox.Text.Trim(),
            LiveExtractThreads = () => Volatile.Read(ref _liveExtractThreads),
            LiveDownloadThreads = () => Volatile.Read(ref _liveDownloadThreads),
            OnDownloadConcurrencyAdjusted = OnDownloadTuned,
            AutoDownshift = _settings.AutoDownshift,
            PartitionSelector = PickPartitions,
            ImageSelector = PickImages,
            OnlyImages = ParseOnlyImages(),
        };

        var progress = new Progress<PipelineEvent>(OnProgress);

        try
        {
            PipelineResult result = await Task.Run(
                () => ExtractPipeline.RunAsync(options, progress, _cts.Token), _cts.Token);

            FillResults(result);
            _lastOutputDirectory = result.OutputDirectory;
            _openOutput.Enabled = true;
            Volatile.Write(ref _progressSealed, 1);
            _progress.Value = 1;
            _status.Text = "完成";
            _statusText.Text = result.OutputDirectory;

            double seconds = Math.Max(0.001, result.Elapsed.TotalSeconds);
            string avgSpeed = ExtractPipeline.FormatSize((long)(result.TotalBytes / seconds)) + "/s";
            _status.Text = $"完成 · 用时 {seconds:F1}s · 平均 {avgSpeed}";
            AppendLog($"完成：{DescribeResult(result)}，用时 {seconds:F1} 秒，平均 {avgSpeed}");

            // --auto 是脚本/批量用的，弹窗会卡住自动化，只给手动操作弹
            if (!_autoStart)
            {
                using var done = new DoneDialog("提取成功", $"耗时 {seconds:F1} 秒");
                done.ShowDialog(this);
            }
            AppendLog($"输出目录：{result.OutputDirectory}");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已取消";
            AppendLog("已取消。");
        }
        catch (Exception ex)
        {
            _status.Text = "失败";
            string detail = DescribeFailure(ex);
            AppendLog("失败：" + detail);
            if (!_autoStart)
                MessageBox.Show(this, detail, "提取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Volatile.Write(ref _progressSealed, 1);
            _stallWatch.Stop();
            _dotsTimer.Stop();
            _progress.Running = false;
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void AdvanceDots()
    {
        _dotsPhase = _dotsPhase % 3 + 1;
        _status.Text = _dotsBase + new string('.', _dotsPhase);
    }

    private int _lastVerifyPct = -1;

    // 进度事件由 Progress<T> 走线程池异步投递，到达顺序不保证：生成时刻的字节数是单调的，
    // 但 UI 可能先收到后生成的那条，直接赋值会让进度条和进度列往回跳。
    // 下面这几个峰值只允许前进，新一轮提取开始时由 ResetProgressPeaks() 清掉基准，
    // 免得把上一轮的值当成下限。
    private long _overallBytesPeak;
    private readonly Dictionary<string, double> _partitionPercentPeak = new();
    private readonly Dictionary<string, long> _partitionSizePeak = new();

    /// <summary>本次选中的分区总数（命令行 --parts 或用户勾选）：进度条按它摊平。</summary>
    private int _partitionTotal;

    /// <summary>1 表示本轮提取已经结束，队列里迟到的事件不许再动进度条。</summary>
    private int _progressSealed;

    private void ResetProgressPeaks()
    {
        _overallBytesPeak = 0;
        _partitionPercentPeak.Clear();
        _partitionSizePeak.Clear();
        Volatile.Write(ref _partitionTotal, 0);
        Volatile.Write(ref _progressSealed, 0);
    }

    /// <summary>
    /// 把各分区的峰值百分比摊到"本次选中的分区总数"上，得到整体进度。
    /// 还没开始的分区按 0 算，所以并行提取时进度条是稳的：
    /// 单分区时它恰好等于这一行的进度，多分区时它单调、不虚高、也不会卡在 100%。
    /// </summary>
    private void UpdateOverallFromPartitions()
    {
        int total = Volatile.Read(ref _partitionTotal);
        if (total <= 0)
            return;

        double sum = 0;
        foreach (double peak in _partitionPercentPeak.Values)
            sum += peak;

        _progress.Value = Math.Clamp(sum / (total * 100.0), 0.0, 1.0);
    }

    private void OnProgress(PipelineEvent e)
    {
        // 本轮已经收尾（完成/取消/失败），Progress<T> 队列里迟到的事件不许再改界面
        if (Volatile.Read(ref _progressSealed) != 0)
            return;

        if (e.BytesDone > 0)
            Interlocked.Exchange(ref _lastProgressTicks, Environment.TickCount64);

        // 连接/探测阶段：点号轮播，别让界面看起来像卡住
        bool connecting = e.Stage is PipelineStage.Probing && !e.IsPartitionProgress;
        if (connecting)
        {
            _dotsBase = e.Message.TrimEnd('.', '…', ' ');
            if (_dotsBase.Length == 0)
                _dotsBase = "正在连接服务器";
            _dotsPhase = 0;
            AdvanceDots();
            _detail.Text = e.Message;
            if (!_dotsTimer.Enabled)
                _dotsTimer.Start();
        }
        else
        {
            _dotsTimer.Stop();
        }

        // 校验阶段：第二行给实时百分比，日志每 10% 记一条，进度条保持提取完成的状态
        if (e.Stage is PipelineStage.Verifying && e.Message.StartsWith("校验", StringComparison.Ordinal))
        {
            _detail.Text = e.Message;

            if (e.Partition is { Length: > 0 })
            {
                if (e.Message.Contains("校验一致", StringComparison.Ordinal))
                    SetRowState(e.Partition, "完成", "sha256 一致");
                else if (e.BytesTotal > 0)
                    SetRowState(e.Partition, "校验中", null);
            }

            if (e.BytesTotal > 0)
            {
                int pct = (int)(e.BytesDone * 100 / e.BytesTotal);
                if (pct >= _lastVerifyPct + 10 || (pct >= 100 && _lastVerifyPct < 100))
                {
                    _lastVerifyPct = pct;
                    AppendLog($"[校验] {e.Message}");
                }
            }
            else if (_lastVerifyPct < 100)
            {
                _lastVerifyPct = 100;
                AppendLog($"[校验] {e.Message}");
            }

            return;
        }

        // 分区提取完成：百分比收满、状态改完成
        if (e.IsPartitionProgress && e.BytesTotal <= 0 && e.Message.StartsWith("✓", StringComparison.Ordinal))
        {
            _status.Text = e.Message;
            if (e.Partition is { Length: > 0 })
            {
                SetRowState(e.Partition, "完成", null);
                AppendLog($"[提取] ✓ {e.Partition} 提取完成");
            }
        }
        else if (e.IsPartitionProgress)
        {
            // 第一行（粗体）：最近收到进度事件的那个分区
            _status.Text = e.Message;
            if (e.Partition is { Length: > 0 })
                UpdatePartitionRow(e.Partition, e.BytesDone, e.BytesTotal, e.Fraction);

            UpdateOverallFromPartitions();
        }
        else if (!connecting)
        {
            // 第二行（灰字）：速度 + 总量
            _detail.Text = e.Message;
            if (e.Stage is PipelineStage.Downloading)
                _status.Text = "正在下载";
            else if (e.Stage is not PipelineStage.Extracting)
                _status.Text = e.Message;

            // 整包字节事件在多分区并行时不会被来回切，比分区事件稳；
            // 只勾了一个分区时两者口径相同，这时拿它刷新那一行。
            if (e.BytesTotal > 0)
                UpdateSingleRunningRow(e.BytesDone, e.BytesTotal);
        }

        // 日志：阶段变化记一条；同一阶段里换了分区也记一条，不然并行提取时日志只有最开始那个分区
        // 并行提取时分区会来回切，只看"上一个分区"会把同一个分区重复记好几条。
        // 用集合保证每个分区只记一次。
        bool partitionChanged = e.IsPartitionProgress && e.Partition is { Length: > 0 }
            && _loggedPartitions.Add(e.Partition);

        if (e.Stage != _lastLoggedStage || partitionChanged)
        {
            _lastLoggedStage = e.Stage;
            // 分区进度每帧都在变，日志只记"开始提取哪个分区"。
            // 把当时的 MB 数写进日志会很快过期，跟进度区对不上。
            string logged = partitionChanged && e.Partition is { Length: > 0 }
                ? $"正在提取 {e.Partition}"
                : e.Message;
            AppendLog($"[{StageName(e.Stage)}] {logged}");
        }
    }

    /// <summary>
    /// 提取过程中每个分区先占一行，百分比实时刷新。
    /// 这样"卡住"和"只是慢"能一眼分清：百分比在动就是在干活。
    /// </summary>
    private void SetRowState(string name, string state, string? remark)
    {
        if (!_partitionRows.TryGetValue(name, out int index) || index >= _grid.Rows.Count)
            return;

        DataGridViewRow row = _grid.Rows[index];
        row.Cells["state"].Value = state;
        if (state == "完成" && row.Cells["percent"].Value is string pctText && pctText != "100%")
            row.Cells["percent"].Value = "100%";
        if (remark is not null)
            row.Cells["note"].Value = remark;
    }

    private void UpdatePartitionRow(string name, long done, long total, double fraction)
    {
        bool isNew = !_partitionRows.TryGetValue(name, out int index) || index >= _grid.Rows.Count;
        if (isNew)
        {
            // 列顺序：分区 / 大小 / 状态 / 进度 / 耗时 / 备注
            index = _grid.Rows.Add(name, "-", "提取中", "0.0%", "-", string.Empty);
            _partitionRows[name] = index;
        }

        DataGridViewRow row = _grid.Rows[index];

        // PipelineEvent.Fraction 是 0~1 的比例，这里换算成百分比。
        // "开始提取"那条事件的 total 还是 0，不能拿来算百分比，否则会显示成 55%。
        if (total <= 0)
            return;

        double percent = Math.Clamp(fraction * 100, 0, 100);

        // 同一批事件可能乱序到达，这里也只允许前进：分区大小取见过的最大值。
        if (!_partitionSizePeak.TryGetValue(name, out long sizePeak) || total > sizePeak)
        {
            _partitionSizePeak[name] = total;
            row.Cells["size"].Value = ExtractPipeline.FormatSize(total);
        }

        if (_partitionPercentPeak.TryGetValue(name, out double percentPeak) && percent <= percentPeak)
            return;

        _partitionPercentPeak[name] = percent;

        // 大分区慢速时一分钟可能都涨不到 1%，整数百分比看着像卡死，所以留一位小数。
        row.Cells["percent"].Value = percent >= 100 ? "100%" : $"{percent:F1}%";
        row.Cells["state"].Value = percent >= 99.5 ? "收尾" : "提取中";
    }

    /// <summary>
    /// 用整包字节进度刷新"唯一一个正在提取的行"。
    /// 只在恰好一行处于提取中时生效：这时整包总量就是这一行的量，口径一致；
    /// 多行并行时这份总量算到谁头上都不对，宁可不显示，也不能把总进度写进单行。
    /// </summary>
    private void UpdateSingleRunningRow(long done, long total)
    {
        if (total <= 0 || done <= 0)
            return;

        // 整包字节事件同样可能乱序到达：只允许前进。
        if (done <= _overallBytesPeak)
            return;

        _overallBytesPeak = done;

        DataGridViewRow? only = null;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells["state"].Value as string is not ("提取中" or "收尾"))
                continue;

            if (only is not null)
                return;

            only = row;
        }

        if (only is null)
            return;

        double percent = Math.Clamp((double)done / total * 100, 0, 100);
        int bucket = (int)(percent * 2);   // 每 0.5% 才动一次界面
        if (only.Tag is int previous && previous == bucket)
            return;

        only.Tag = bucket;

        // 和 UpdatePartitionRow 共用同一行的百分比峰值：两条路径谁先到，都不能让进度倒退。
        string? name = only.Cells["name"].Value as string;
        if (name is not null)
        {
            if (_partitionPercentPeak.TryGetValue(name, out double percentPeak) && percent <= percentPeak)
                return;

            _partitionPercentPeak[name] = percent;
        }

        only.Cells["percent"].Value = percent >= 100 ? "100%" : $"{percent:F1}%";
    }

    private void FillResults(PipelineResult result)
    {
        _grid.Rows.Clear();
        _partitionRows.Clear();

        if (result.Partitions.Count > 0)
        {
            foreach (var part in result.Partitions.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                string state = part.Skipped ? "跳过" : part.Success ? "成功" : "失败";
                string note = part.Error
                              ?? (part.HashVerified == true ? "sha256 一致"
                                  : part.HashVerified == false ? "sha256 不一致" : string.Empty);

                int index = _grid.Rows.Add(
                    part.Name,
                    ExtractPipeline.FormatSize(part.Size),
                    state,
                    part.Success ? "100%" : "-",
                    part.Elapsed.TotalSeconds > 0 ? $"{part.Elapsed.TotalSeconds:F1}s" : "-",
                    note);

                if (!part.Success && !part.Skipped)
                    _grid.Rows[index].DefaultCellStyle.ForeColor = Color.FromArgb(200, 60, 60);
            }
        }
        else if (result.Images.Count > 0)
        {
            foreach (var image in result.Images)
            {
                int index = _grid.Rows.Add(
                    image.OutputName,
                    ExtractPipeline.FormatSize(image.Size),
                    image.Error is null ? "成功" : "失败",
                    image.Error is null ? "100%" : "-",
                    image.Elapsed.TotalSeconds > 0 ? $"{image.Elapsed.TotalSeconds:F1}s" : "-",
                    image.Error ?? image.EntryName);

                if (image.Error is not null)
                    _grid.Rows[index].DefaultCellStyle.ForeColor = Color.FromArgb(200, 60, 60);
            }

            foreach (string superPartition in result.SuperPartitions)
            {
                _grid.Rows.Add(superPartition, "-", "super 拆分", "-", "-", "来自 super.img");
            }
        }
    }

    private static string DescribeResult(PipelineResult result)
    {
        if (result.Partitions.Count > 0)
        {
            string text = $"{result.PartitionSuccessCount} 个分区，共 {ExtractPipeline.FormatSize(result.TotalBytes)}";
            if (result.PartitionFailedCount > 0)
                text += $"，{result.PartitionFailedCount} 个失败";
            return text;
        }

        int ok = result.Images.Count(i => i.Error is null);
        string summary = $"{ok} 个镜像，共 {ExtractPipeline.FormatSize(result.TotalBytes)}";
        if (result.SuperPartitions.Count > 0)
            summary += $"，super 拆出 {result.SuperPartitions.Count} 个逻辑分区";
        return summary;
    }

    private static string StageName(PipelineStage stage) => stage switch
    {
        PipelineStage.Probing => "连接",
        PipelineStage.Downloading => "下载",
        PipelineStage.Inspecting => "识别",
        PipelineStage.Extracting => "提取",
        PipelineStage.Unpacking => "解包",
        PipelineStage.Verifying => "校验",
        PipelineStage.Completed => "完成",
        PipelineStage.Failed => "失败",
        PipelineStage.Cancelled => "取消",
        _ => "状态",
    };

    private void SetBusy(bool busy)
    {
        _start.Enabled = !busy;
        _browse.Enabled = !busy;
        _source.Enabled = !busy;
        _cancel.Enabled = busy;
        _exThreads.Enabled = true;   // 提取线程允许运行中调，实时生效
        _dlThreads.Enabled = true;   // 下载线程同样实时生效
        _verify.Enabled = !busy;
        _directProxy.Enabled = !busy;
        _onlyImages.Enabled = !busy;
        _outputDir.Enabled = !busy;
        _browseOutput.Enabled = !busy;
        _settingsButton.Enabled = !busy;
        _unpackSuper.Enabled = !busy;
        _keepSuper.Enabled = !busy;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }
}
