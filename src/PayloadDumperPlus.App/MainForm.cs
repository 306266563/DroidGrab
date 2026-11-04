using System.Text;
using PayloadDumperPlus.App.Ui;
using PayloadDumperPlus.Core.Download;
using PayloadDumperPlus.Core.Payload;
using PayloadDumperPlus.Core.Pipeline;

namespace PayloadDumperPlus.App;

public sealed partial class MainForm : Form
{
    private const int Pad = 14;

    private readonly TextBox _source = new();
    private readonly TextBox _outputDir = new();
    private readonly FlatButton _browseOutput = new();
    private readonly FlatButton _settingsButton = new();
    private readonly FlatButton _browse = new();
    private readonly FlatButton _start = new();
    private readonly FlatButton _cancel = new();

    private readonly FlatSlider _dlThreads = new();
    private readonly FlatSlider _exThreads = new();
    private readonly Label _dlValue = new();
    // 自动调档正在改滑块时置 1，此时 ValueChanged 不能当成"用户拖了滑块"
    private long _tuningFromCore;
    private readonly Label _exValue = new();

    private readonly FlatCheckBox _verify = new();
    private readonly FlatCheckBox _directProxy = new();
    private readonly FlatCheckBox _unpackSuper = new();
    private readonly FlatCheckBox _keepSuper = new();
    private readonly TextBox _onlyImages = new();
    private readonly TextBox _proxyBox = new();

    private readonly ProgressStrip _progress = new();
    private readonly System.Windows.Forms.Timer _stallWatch = new() { Interval = 600 };
    private long _lastProgressTicks;
    private bool _stallNotified;
    private long _lastStallLogTicks;
    // 弹出分区/镜像选择框时置 1：这段时间没有网络活动是正常的，看门狗不该报重连
    private int _awaitingInput;
    private int _liveExtractThreads;
    private int _liveDownloadThreads;
    private readonly Label _status = new();
    private readonly Label _detail = new();

    // 连接阶段点号轮播：正在连接服务器. / .. / ... 循环，防止看起来像卡死
    private readonly System.Windows.Forms.Timer _dotsTimer = new() { Interval = 400 };
    private int _dotsPhase;
    private string _dotsBase = string.Empty;

    private readonly DataGridView _grid = new();
    private readonly TextBox _log = new();

    private readonly ToolStripStatusLabel _statusText = new();
    private readonly ToolStripButton _openOutput = new();

    private CancellationTokenSource? _cts;
    private bool _closing;

    /// <summary>UI 线程维护的「只解这些镜像」文本快照：后台提取线程只读它，不碰控件。</summary>
    private volatile string _onlyImagesText = string.Empty;

    private string? _lastOutputDirectory;
    private PipelineStage _lastLoggedStage = PipelineStage.Idle;
    private readonly HashSet<string> _loggedPartitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _partitionRows = new(StringComparer.Ordinal);
    private readonly bool _autoStart;
    private readonly string? _preferredOutputDirectory;

    private readonly IReadOnlyCollection<string>? _forcedPartitions;
    private AppSettings _settings = new();

    public MainForm(string? initialSource, bool autoStart = false, string? outputDirectory = null,
        IReadOnlyCollection<string>? forcedPartitions = null)
    {
        _dotsTimer.Tick += (_, _) => AdvanceDots();

        // 提取线程运行中可调：滑块一变就写进共享值，提取器每次取任务都会读一次
        _liveExtractThreads = _exThreads.Value;
        _liveDownloadThreads = _dlThreads.Value;
        _exThreads.ValueChanged += (_, _) => Volatile.Write(ref _liveExtractThreads, _exThreads.Value);
        _dlThreads.ValueChanged += (_, _) =>
        {
            // 自动调档会改滑块位置，那种改动不是用户意图，别覆盖用户设的上限
            if (Interlocked.Read(ref _tuningFromCore) != 0)
                return;
            Volatile.Write(ref _liveDownloadThreads, _dlThreads.Value);
        };
        _autoStart = autoStart && !string.IsNullOrWhiteSpace(initialSource);
        _preferredOutputDirectory = outputDirectory;
        _forcedPartitions = forcedPartitions;

        Text = "DroidGrab · 安卓分区提取器";
        ClientSize = new Size(1060, 730);
        MinimumSize = new Size(900, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Ui(9F);
        BackColor = Theme.Surface;
        DoubleBuffered = true;
        AllowDrop = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(Pad),
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        root.Controls.Add(BuildInputPanel(), 0, 0);
        root.Controls.Add(BuildOptionsPanel(), 0, 1);
        root.Controls.Add(BuildProgressPanel(), 0, 2);
        root.Controls.Add(BuildSplit(), 0, 3);
        root.Controls.Add(BuildStatusStrip(), 0, 4);

        Controls.Add(root);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _browse.Click += OnBrowseClicked;
        _browseOutput.Click += OnBrowseOutputClicked;
        _settingsButton.Click += OnSettingsClicked;
        _settings = AppSettings.Load();
        if (!string.IsNullOrWhiteSpace(_settings.OutputDirectory))
            _outputDir.Text = _settings.OutputDirectory;

        // 命令行 --out 优先于设置里的值，而且要让用户看得见当前输出目录
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            _outputDir.Text = outputDirectory;

        _start.Click += OnStartClicked;
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _status.Text = "正在取消…";
            _cts?.Cancel();
        };
        _openOutput.Click += (_, _) => OpenOutputDirectory();
        _grid.CellDoubleClick += OnGridDoubleClick;

        _stallWatch.Tick += (_, _) =>
        {
            if (!_progress.Running)
            {
                _stallNotified = false;
                return;
            }

            // 正在等用户勾分区/镜像：没有网络活动是正常的，把水位推到现在
            if (Interlocked.CompareExchange(ref _awaitingInput, 0, 0) != 0)
            {
                Interlocked.Exchange(ref _lastProgressTicks, Environment.TickCount64);
                _progress.Stalled = false;
                _stallNotified = false;
                return;
            }

            long idle = Environment.TickCount64 - Interlocked.Read(ref _lastProgressTicks);
            _progress.Stalled = idle > 8000;

            // 长时间没有字节进出时说清楚在等什么，别让人以为程序死了
            // 卡住提示：一条就够，60 秒内不重复刷，恢复后重新计数
            if (idle >= 20000 && !_stallNotified
                && Environment.TickCount64 - _lastStallLogTicks >= 60000)
            {
                _stallNotified = true;
                _lastStallLogTicks = Environment.TickCount64;
                AppendLog("[重试] 连接 20 秒没有新数据，正在自动重连；单次请求 30 秒无数据会自动重试。");
            }
            else if (idle < 3000)
            {
                _stallNotified = false;
            }
        };

        if (!string.IsNullOrWhiteSpace(initialSource))
            _source.Text = initialSource;

        AppendLog("就绪。支持 payload.bin、OTA 完整包 zip、线刷包 tar/tgz/zip、sparse img，以及 http(s) 直链。");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        CaptureBackdrop();

        // 启动别把焦点塞在输入框里，否则 PlaceholderText 会被 WinForms 自动隐藏，用户以为没写提示
        ActiveControl = null;

        if (_autoStart)
            BeginInvoke(new Action(() => OnStartClicked(null, EventArgs.Empty)));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closing)
        {
            base.OnFormClosing(e);
            return;
        }

        CancellationTokenSource? cts = _cts;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            // 关窗时流水线还在跑：先说清代价，确认后取消，并给它最多 3 秒收尾。
            if (e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult answer = MessageBox.Show(
                    this,
                    "正在提取，确定要退出吗？没下完的文件会被丢掉。",
                    "还在提取",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _closing = true;
            try
            {
                cts.Cancel();
                long deadline = Environment.TickCount64 + 3000;
                while (_cts is not null && Environment.TickCount64 < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(40);
                }
            }
            finally
            {
                _closing = false;
            }
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stallWatch.Stop();
            _stallWatch.Dispose();
            _dotsTimer.Stop();
            _dotsTimer.Dispose();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _screenBlur?.Dispose();
            _screenBlur = null;
        }

        base.Dispose(disposing);
    }
}
