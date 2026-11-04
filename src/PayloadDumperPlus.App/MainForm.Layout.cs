using System.Drawing.Drawing2D;
using System.Text;
using PayloadDumperPlus.Core.Download;
using PayloadDumperPlus.Core.Payload;
using PayloadDumperPlus.Core.Pipeline;
using PayloadDumperPlus.App.Ui;

namespace PayloadDumperPlus.App;

public sealed partial class MainForm
{
    // ---------- 布局 ----------

    private Control BuildInputPanel()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 5,
            RowCount = 2,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(4, 4, 4, 10),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        Label RowLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(90, 98, 112),
        };

        _source.Dock = DockStyle.Fill;
        _source.Font = new Font("Consolas", 9.5F);
        _source.PlaceholderText = @"D:\rom\payload.bin   或   https://example.com/ota.zip";

        _browse.Text = "浏览…";
        _browse.Dock = DockStyle.Fill;

        _start.Text = "开始提取";
        _start.Dock = DockStyle.Fill;
        _start.Primary = true;

        _cancel.Text = "取消";
        _cancel.Dock = DockStyle.Fill;
        _cancel.Enabled = false;

        _outputDir.Dock = DockStyle.Fill;
        _outputDir.Font = new Font("Consolas", 9F);
        _outputDir.PlaceholderText = "留空 = 自动（包所在目录 / extracted_包名）";

        _browseOutput.Text = "选择…";
        _browseOutput.Dock = DockStyle.Fill;

        _settingsButton.Text = "设置";
        _settingsButton.Dock = DockStyle.Fill;

        grid.Controls.Add(RowLabel("包路径 / URL"), 0, 0);
        grid.Controls.Add(WrapWithClear(_source), 1, 0);
        grid.Controls.Add(_browse, 2, 0);
        grid.Controls.Add(_start, 3, 0);
        grid.Controls.Add(_cancel, 4, 0);

        grid.Controls.Add(RowLabel("输出目录"), 0, 1);
        grid.Controls.Add(WrapWithClear(_outputDir), 1, 1);
        grid.Controls.Add(_browseOutput, 2, 1);
        var settingsHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _settingsButton.Dock = DockStyle.Fill;
        settingsHost.Controls.Add(_settingsButton);
        grid.Controls.Add(settingsHost, 3, 1);
        grid.SetColumnSpan(settingsHost, 2);

        RegisterCard(grid);

        return grid;
    }

    /// <summary>给输入框右边挂一个只在有内容时出现的清空按钮。</summary>
    private static Control WrapWithClear(TextBox box)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(3, 3, 3, 3) };
        box.Dock = DockStyle.Fill;

        var clear = new FlatButton
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 26,
            // 同「…」按钮：默认 ButtonRadius=19 比按钮本身还高，圆角会失控
            Radius = 8,
            ForeColor = Theme.TextSecondary,
            Visible = false,
            TabStop = false,
        };
        clear.FlatAppearance.BorderSize = 0;

        clear.Click += (_, _) =>
        {
            box.Clear();
            box.Focus();
        };

        box.TextChanged += (_, _) => clear.Visible = box.Text.Length > 0;

        host.Controls.Add(box);
        host.Controls.Add(clear);
        return host;
    }

    private Control BuildOptionsPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(4, 0, 4, 10),
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Color.Transparent,
        };

        flow.Controls.Add(BuildSliderGroup("下载线程", _dlThreads, _dlValue, Math.Clamp(Environment.ProcessorCount * 2, 8, 16), out _));
        flow.Controls.Add(BuildSliderGroup("提取线程", _exThreads, _exValue,
            Math.Clamp(Environment.ProcessorCount, 4, 32), out _));

        // 行距必须比 FlatCheckBox 的 22px 高出一截，不然上下两行勾选框只隔 2px，看着像叠在一起。
        var checks = new Panel { Width = 396, Height = 108, BackColor = Theme.CardFill };

        _verify.Text = "校验分区哈希";
        _verify.Checked = true;
        _verify.Location = new Point(0, 2);
        _verify.AutoSize = true;

        _directProxy.Text = "直连（不用系统代理）";
        _directProxy.Checked = false;
        _directProxy.Location = new Point(150, 2);
        _directProxy.AutoSize = true;

        _unpackSuper.Text = "拆开 super.img";
        _unpackSuper.Checked = true;
        _unpackSuper.Location = new Point(0, 34);
        _unpackSuper.AutoSize = true;

        _keepSuper.Text = "保留 super.img 原件";
        _keepSuper.Checked = true;
        _keepSuper.Location = new Point(150, 34);
        _keepSuper.AutoSize = true;

        checks.Controls.AddRange(new Control[] { _verify, _directProxy, _unpackSuper, _keepSuper });

        var info = new Label
        {
            Text = "线刷包只解这些（逗号分隔，留空=全部）：",
            Location = new Point(0, 62),
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 98, 112),
        };
        checks.Controls.Add(info);

        _onlyImages.Location = new Point(0, 84);
        _onlyImages.Width = 196;
        _onlyImages.Font = new Font("Consolas", 9F);
        _onlyImages.PlaceholderText = "boot.img,super.img";
        // 提取线程会在后台回调 PickImages，这里在 UI 线程给它留一份文本快照。
        _onlyImages.TextChanged += (_, _) => _onlyImagesText = _onlyImages.Text;
        checks.Controls.Add(_onlyImages);

        var proxyLabel = new Label
        {
            Text = "代理",
            Location = new Point(204, 88),
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 98, 112),
        };
        checks.Controls.Add(proxyLabel);

        _proxyBox.Location = new Point(238, 84);
        _proxyBox.Width = 150;
        _proxyBox.Font = new Font("Consolas", 9F);
        _proxyBox.PlaceholderText = "127.0.0.1:7897";
        checks.Controls.Add(_proxyBox);

        var tip = new ToolTip();
        tip.SetToolTip(_proxyBox,
            "填了就按这份代理走（例如 127.0.0.1:7897）。留空且不勾“直连”时跟随 Windows 系统代理设置；系统里没开代理就等于直连。");
        tip.SetToolTip(_directProxy, "勾上强制直连，忽略系统代理和上面的代理框。");

        flow.Controls.Add(checks);
        panel.Controls.Add(flow);
        RegisterCard(panel);
        return panel;
    }

    private Control BuildSliderGroup(string caption, FlatSlider bar, Label value, int initial, out Panel group)
    {
        group = new Panel { Width = 264, Height = 96, BackColor = Color.Transparent };

        var label = new Label
        {
            Text = caption,
            Location = new Point(0, 20),
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 98, 112),
        };

        value.Text = initial.ToString();
        value.Location = new Point(62, 20);
        value.AutoSize = true;
        value.Font = Theme.Ui(9F, FontStyle.Bold);
        value.ForeColor = Color.FromArgb(37, 99, 235);

        bar.Minimum = 1;
        bar.Maximum = 32;
        bar.Value = Math.Clamp(initial, 1, 32);
        bar.SmallChange = 1;
        bar.LargeChange = 4;
        bar.Location = new Point(0, 48);
        bar.Width = 244;
        bar.AutoSize = false;
        bar.Height = 40;
        bar.ValueChanged += (_, _) => value.Text = bar.Value.ToString();

        group.Controls.Add(label);
        group.Controls.Add(value);
        group.Controls.Add(bar);
        return group;
    }

    private Control BuildProgressPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(4, 0, 4, 10),
        };

        _progress.Dock = DockStyle.Top;
        _progress.Height = 10;

        _status.Dock = DockStyle.Top;
        _status.Height = 22;
        _status.Text = "待机";
        _status.Font = Theme.Ui(9.5F, FontStyle.Bold);
        _status.ForeColor = Theme.TextPrimary;

        _detail.Dock = DockStyle.Top;
        _detail.Height = 20;
        _detail.ForeColor = Theme.TextSecondary;

        panel.Controls.Add(_detail);
        panel.Controls.Add(_status);
        panel.Controls.Add(_progress);

        RegisterCard(panel);

        return panel;
    }

    private Control BuildSplit()
    {
        // 表格 + 日志合成一张大 R 角卡片。
        // 外层 host 走 RegisterCard，交给窗体的 Backdrop 统一画圆角底 + 描边，跟别的卡片同款、带抗锯齿；
        // 里面的 SplitContainer 只要 1px 内缩 + 自己裁圆角 Region，就不会用方角盖掉卡片的弧线。
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(1, 1, 1, 1),
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 8,
            BackColor = Theme.CardFill,
        };

        split.Panel2.BackColor = Theme.CardFill;
        // 表格和日志之间留一条 1px 分隔线：靠 Panel1 底边的 padding 露出它自己的 BackColor。
        split.Panel1.BackColor = Theme.CardBorder;
        split.Panel1.Padding = new Padding(0, 0, 0, 1);

        split.SizeChanged += (_, _) => ApplyRoundedRegion(split, Theme.Scale(split, Theme.CardRadius));
        ApplyRoundedRegion(split, Theme.Scale(split, Theme.CardRadius));

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Theme.CardFill;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
        // 不关掉这个，表头默认画一圈 Raised 的深灰 3D 边，会在卡片顶上横出一条突兀的黑线。
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.GridColor = Color.FromArgb(238, 241, 246);
        _grid.RowTemplate.Height = 34;
        _grid.ColumnHeadersHeight = 40;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.CardFill;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextSecondary;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.CardFill;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.TextSecondary;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.Ui(9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 0, 10, 0);
        _grid.DefaultCellStyle.Font = Theme.Ui(9F);
        _grid.DefaultCellStyle.ForeColor = Theme.TextPrimary;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.RowSelected;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.TextPrimary;
        _grid.DefaultCellStyle.Padding = new Padding(10, 0, 10, 0);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.RowAlt;
        _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.RowSelected;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "分区 / 镜像", FillWeight = 32 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "大小", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "percent", HeaderText = "进度", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "time", HeaderText = "耗时", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "note", HeaderText = "备注", FillWeight = 28 });

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Theme.CardFill;
        _log.ForeColor = Theme.LogText;
        _log.Font = new Font("Consolas", 9F);
        _log.BorderStyle = BorderStyle.None;

        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_log);
        split.Panel2.Padding = new Padding(0, 6, 0, 0);

        split.HandleCreated += (_, _) =>
        {
            try { split.SplitterDistance = (int)(split.Height * 0.55); }
            catch (InvalidOperationException) { }
        };

        host.Controls.Add(split);
        RegisterCard(host);
        return host;
    }

    /// <summary>
    /// 把控件裁成圆角矩形。Region 是硬边裁剪，会有像素级锯齿，
    /// 所以只用在表格这种整块背景上；按钮的圆角一律靠自绘，不用 Region。
    /// </summary>
    private static void ApplyRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 2 || control.Height <= 2)
            return;

        using GraphicsPath path = Drawing.Rounded(
            new Rectangle(0, 0, control.Width, control.Height), radius);
        Region? old = control.Region;
        control.Region = new Region(path);
        old?.Dispose();
    }

    private Control BuildStatusStrip()
    {
        var strip = new StatusStrip
        {
            Dock = DockStyle.Fill,
            SizingGrip = false,
            BackColor = Color.Transparent,
        };

        _statusText.Text = $"v{Application.ProductVersion} · 绿色版";
        _statusText.Spring = true;
        _statusText.TextAlign = ContentAlignment.MiddleLeft;

        _openOutput.Text = "打开输出目录";
        // 常驻可点：还没有输出目录时点它会给出提示，而不是「点了没反应」
        _openOutput.Enabled = true;
        // 底部两个文字按钮统一成正文色，跟状态文字保持一致。
        _openOutput.ForeColor = Theme.TextPrimary;

        var reward = new ToolStripButton
        {
            Text = "赞赏",
            ForeColor = Theme.TextPrimary,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };
        reward.Click += (_, _) =>
        {
            using var dialog = new RewardDialog();
            dialog.ShowDialog(this);
        };

        strip.Items.Add(_statusText);
        strip.Items.Add(_openOutput);
        strip.Items.Add(reward);
        return strip;
    }

}
