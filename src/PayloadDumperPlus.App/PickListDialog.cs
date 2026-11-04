namespace PayloadDumperPlus.App;

/// <summary>
/// 通用勾选对话框：选分区、选线刷包里的镜像都用它。
/// 用 DataGridView 而不是 CheckedListBox：后者一行只有一个拼好的字符串，
/// 窗口一宽右边就空出一大片；表格能把列撑满，还能按大小排序。
/// </summary>
public sealed class PickListDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly TextBox _search = new();

    public IReadOnlyCollection<string> Selected { get; private set; } = Array.Empty<string>();

    public PickListDialog(string title, string hint, IReadOnlyList<(string Name, double SizeMb)> items)
    {
        Text = title;
        ClientSize = new Size(620, 560);
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        ShowInTaskbar = false;

        var hintLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(12, 10, 12, 0),
            Text = hint,
            ForeColor = Color.FromArgb(90, 98, 112),
        };

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.GridColor = Color.FromArgb(238, 240, 244);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 30;
        _grid.RowTemplate.Height = 28;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 242, 246);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

        var searchHost = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 6, 12, 6) };
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "搜索名称…";
        _search.Font = new Font("Microsoft YaHei UI", 9F);

        // 一键清空：省得按住退格删
        var clear = new Button
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 28,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(120, 126, 138),
            Visible = false,
        };
        clear.FlatAppearance.BorderSize = 0;
        clear.Click += (_, _) =>
        {
            _search.Clear();
            _search.Focus();
        };
        _search.TextChanged += (_, _) => clear.Visible = _search.Text.Length > 0;

        searchHost.Controls.Add(_search);
        searchHost.Controls.Add(clear);

        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "check",
            HeaderText = string.Empty,
            FillWeight = 8,
            MinimumWidth = 38,
            Resizable = DataGridViewTriState.False,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name",
            HeaderText = "名称",
            FillWeight = 62,
            ReadOnly = true,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "size",
            HeaderText = "大小",
            FillWeight = 30,
            ReadOnly = true,
        });
        _grid.Columns[2].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

        foreach ((string name, double mb) in items)
        {
            int rowIndex = _grid.Rows.Add(true, name, mb >= 1024 ? $"{mb / 1024:F2} GB" : $"{mb:F1} MB");
            _grid.Rows[rowIndex].Tag = mb;
        }

        // 点整行就能切换勾选，不用非得点那个小方框
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0)
                return;

            if (e.ColumnIndex == 0)
                return;

            DataGridViewCell cell = _grid.Rows[e.RowIndex].Cells[0];
            cell.Value = !(cell.Value is true);
        };

        _search.TextChanged += (_, _) => ApplyFilter();

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(12, 9, 12, 9) };

        var ok = new Button { Text = "开始提取", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 112 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 82 };
        var all = new Button { Text = "全选", Dock = DockStyle.Left, Width = 70 };
        var none = new Button { Text = "全不选", Dock = DockStyle.Left, Width = 76 };
        var small = new Button { Text = "只选小的", Dock = DockStyle.Left, Width = 92 };
        var common = new Button { Text = "常用", Dock = DockStyle.Left, Width = 66 };

        all.Click += (_, _) => SetAll(true);
        none.Click += (_, _) => SetAll(false);
        small.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!row.Visible)
                    continue;

                double mb = row.Tag is double value ? value : 0;
                row.Cells[0].Value = mb <= 128;
            }
        };

        common.Click += (_, _) => PickCommon();

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(all);
        buttons.Controls.Add(none);
        buttons.Controls.Add(small);
        buttons.Controls.Add(common);

        Controls.Add(_grid);
        Controls.Add(searchHost);
        Controls.Add(hintLabel);
        Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            Selected = _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Cells[0].Value is true)
                .Select(r => (string)r.Cells[1].Value)
                .ToArray();

            if (Selected.Count == 0)
            {
                MessageBox.Show(this, "至少勾一个。", "没选", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };
    }

    private void SetAll(bool value)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Visible)
                row.Cells[0].Value = value;
        }
    }

    /// <summary>一键勾上真正会被单独提取的那几个分区，其余不动。</summary>
    private void PickCommon()
    {
        string[] names = { "boot", "vendor", "system", "super", "product", "odm", "system_ext", "vbmeta" };

        foreach (DataGridViewRow row in _grid.Rows)
        {
            string name = (row.Cells[1].Value as string ?? string.Empty).ToLowerInvariant();
            bool hit = names.Any(n => name == n || name == n + "_a" || name == n + "_b" || name.StartsWith(n + "."));
            if (hit)
                row.Cells[0].Value = true;
        }
    }

    private void ApplyFilter()
    {
        string keyword = _search.Text.Trim();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (keyword.Length == 0)
            {
                row.Visible = true;
                continue;
            }

            string name = row.Cells[1].Value as string ?? string.Empty;
            row.Visible = name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }
    }
}
