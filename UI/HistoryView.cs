using System.Data;
using SmartVisionSystem.Core;
using SmartVisionSystem.Infrastructure;
using Size = System.Drawing.Size;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 历史数据（生产主管 / 质量追溯）：按时间、结果、配方、SN 组合查询，支持 CSV 导出。
    /// 追溯的核心是"当时用的是哪版阈值、偏差是多少"——所以记录里同时存了配方名、偏差(mm/px)、得分、面积。
    /// </summary>
    public sealed class HistoryView : UserControl
    {
        private readonly DataGridView _grid = new();
        private readonly ComboBox _cboRange = Field.Combo(new[] { "今天", "近 3 天", "近 7 天", "近 30 天", "全部", "自定义" }, 100);
        private readonly TextBox _txtFrom = Field.Input("", 176);
        private readonly TextBox _txtTo = Field.Input("", 176);
        private readonly ComboBox _cboResult = Field.Combo(new[] { "全部", "OK", "NG" }, 90);
        private readonly ComboBox _cboRecipe = Field.Combo(new[] { "全部" }, 130);
        private readonly TextBox _txtSn = Field.Input("", 130);
        private readonly Label _lblSummary = new();
        private readonly Label _lblDetail = new();
        private DataTable? _table;

        public HistoryView()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;

            BuildGrid();
            BuildDetail();
            BuildSummary();
            BuildFilterBar();

            RefreshRecipeList();
            Query();
        }

        private static DeviceHub Hub => DeviceHub.I;

        // ─────────────────────────── 布局 ───────────────────────────

        private void BuildFilterBar()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Panel, Padding = new Padding(12, 8, 12, 8) };

            // 允许换行：窄屏下筛选条件自动折到第二行，不会被裁掉
            var row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = Color.Transparent };

            var lblRange = Label("时间范围");
            _cboRange.SelectedIndex = 2;
            _cboRange.SelectedIndexChanged += (_, _) => SyncRangeInputs();

            _txtFrom.Text = DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd HH:mm");
            _txtTo.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _txtFrom.Width = 176;
            _txtTo.Width = 176;
            _txtFrom.Font = Theme.Mono(9F);
            _txtTo.Font = Theme.Mono(9F);

            var lblRes = Label("结果");
            var lblRec = Label("配方");
            var lblSn = Label("SN 包含");

            var btnQuery = Theme.Btn("查询", Theme.Accent, Color.White, 84, 30, 9F);
            var btnExport = Theme.Btn("导出 CSV", Theme.Card, Theme.Text, 96, 30, 9F);
            var btnClear = Theme.Btn("清空历史", Theme.Card, Theme.Ng, 96, 30, 9F);

            btnQuery.Click += (_, _) => Query();
            btnExport.Click += (_, _) => Export();
            btnClear.Click += (_, _) => ClearAll();

            foreach (var c in new Control[] { lblRange, _cboRange, _txtFrom, _txtTo, lblRes, _cboResult, lblRec, _cboRecipe, lblSn, _txtSn, btnQuery, btnExport, btnClear })
            {
                c.Margin = c is Label ? new Padding(6, 6, 2, 0) : new Padding(0, 2, 8, 0);
                row1.Controls.Add(c);
            }

            bar.Controls.Add(row1);
            Controls.Add(bar);
        }

        private static Label Label(string text) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = Theme.SubText,
            Font = Theme.F(9F)
        };

        private void BuildSummary()
        {
            var bar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Bg, Padding = new Padding(4, 8, 4, 4) };
            _lblSummary.Dock = DockStyle.Fill;
            _lblSummary.ForeColor = Theme.Cyan;
            _lblSummary.Font = Theme.F(10F, FontStyle.Bold);
            _lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            _lblSummary.Text = "—";
            bar.Controls.Add(_lblSummary);
            Controls.Add(bar);
        }

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            Theme.StyleGrid(_grid);
            _grid.SelectionChanged += (_, _) => ShowDetail();
            _grid.CellDoubleClick += (_, _) => ShowDetail();
            Controls.Add(_grid);
        }

        private void BuildDetail()
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Theme.Card, Padding = new Padding(12, 8, 12, 8) };
            _lblDetail.Dock = DockStyle.Fill;
            _lblDetail.ForeColor = Theme.SubText;
            _lblDetail.Font = Theme.Mono(9F);
            _lblDetail.TextAlign = ContentAlignment.MiddleLeft;
            _lblDetail.Text = "选中一行查看详情（双击可复制该行关键信息到状态栏）";
            bar.Controls.Add(_lblDetail);
            Controls.Add(bar);
        }

        // ─────────────────────────── 业务 ───────────────────────────

        /// <summary>时间范围联动：只有"自定义"才允许手工输入</summary>
        private void SyncRangeInputs()
        {
            bool custom = _cboRange.SelectedItem?.ToString() == "自定义";
            _txtFrom.ReadOnly = !custom;
            _txtTo.ReadOnly = !custom;
            _txtFrom.BackColor = custom ? Color.FromArgb(28, 31, 37) : Color.FromArgb(24, 26, 31);
            _txtTo.BackColor = _txtFrom.BackColor;
        }

        private (DateTime? From, DateTime? To) ResolveRange()
        {
            var now = DateTime.Now;
            return _cboRange.SelectedItem?.ToString() switch
            {
                "今天" => (now.Date, now),
                "近 3 天" => (now.AddDays(-3), now),
                "近 7 天" => (now.AddDays(-7), now),
                "近 30 天" => (now.AddDays(-30), now),
                "自定义" => (ParseTime(_txtFrom.Text, now.AddDays(-7)), ParseTime(_txtTo.Text, now)),
                _ => (null, null)
            };

            static DateTime? ParseTime(string text, DateTime fallback)
                => DateTime.TryParse(text.Trim(), out var t) ? t : fallback;
        }

        private HistoryFilter BuildFilter() => new()
        {
            From = ResolveRange().From,
            To = ResolveRange().To,
            Result = _cboResult.SelectedItem?.ToString(),
            Recipe = _cboRecipe.SelectedItem?.ToString(),
            SnKeyword = _txtSn.Text.Trim(),
            Limit = 2000
        };

        private void RefreshRecipeList()
        {
            var current = _cboRecipe.SelectedItem?.ToString() ?? "全部";
            _cboRecipe.Items.Clear();
            _cboRecipe.Items.Add("全部");
            foreach (var n in RecipeStore.List()) _cboRecipe.Items.Add(n);
            _cboRecipe.SelectedItem = _cboRecipe.Items.Contains(current) ? current : "全部";
        }

        private void Query()
        {
            if (!Hub.IsInitialized) return;
            var filter = BuildFilter();
            _table = Hub.Db.Query(filter);
            _grid.DataSource = _table;

            var (total, ok, ng) = Hub.Db.Summary(filter);
            double yield = total > 0 ? ok * 100.0 / total : 0;
            _lblSummary.Text = $"筛选结果：共 {total} 条    OK {ok}    NG {ng}    良率 {yield:F2}%    （最多显示 {filter.Limit} 条）";
            _lblSummary.ForeColor = total == 0 ? Theme.Muted : yield >= 98 ? Theme.Ok : yield >= 95 ? Theme.Warn : Theme.Ng;

            ApplyColumnLayout();

            Log.Info("历史", $"查询历史数据：{total} 条（OK {ok} / NG {ng}）");
        }

        /// <summary>固定列宽 + 横向滚动：16 列数据用 Fill 模式会被压到只剩"偏差"两个字，必须给足宽度</summary>
        private void ApplyColumnLayout()
        {
            var widths = new Dictionary<string, int>
            {
                ["时间"] = 142, ["流水号"] = 102, ["配方"] = 96, ["结果"] = 56,
                ["X偏差mm"] = 94, ["Y偏差mm"] = 94, ["X偏差px"] = 90, ["Y偏差px"] = 90,
                ["得分"] = 72, ["角度"] = 70, ["面积px²"] = 90, ["表面pct"] = 80,
                ["CTms"] = 76, ["节拍ms"] = 76, ["不良原因"] = 200
            };

            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            if (_grid.Columns.Contains("序号")) _grid.Columns["序号"]!.Visible = false;
            // px 偏差与视觉 CT 仍入库、仍导出 CSV，只是不占表格宽度（详情行会显示），
            // 这样表格宽度能塞进可视区，不出现难看的原生横向滚动条
            foreach (var hidden in new[] { "X偏差px", "Y偏差px", "CTms", "节拍ms" })
                if (_grid.Columns.Contains(hidden)) _grid.Columns[hidden]!.Visible = false;

            var headerFont = _grid.ColumnHeadersDefaultCellStyle.Font ?? Theme.F(9.5F, FontStyle.Bold);
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                if (!col.Visible) continue;
                // 关键：列宽取"业务期望宽度"与"表头实测宽度"的较大者，
                // 这样换字体/放大 DPI 时表头也不会被截成两个字
                int headerWidth = TextRenderer.MeasureText(col.HeaderText, headerFont).Width + 20;
                int want = widths.TryGetValue(col.Name, out var w2) ? w2 : 90;
                col.Width = Math.Max(want, headerWidth);
                col.SortMode = DataGridViewColumnSortMode.Automatic;
            }
        }

        private void ShowDetail()
        {
            if (_grid.CurrentRow?.DataBoundItem is not DataRowView row) return;
            try
            {
                string Get(string key) => row.Row.Table.Columns.Contains(key) ? row[key]?.ToString() ?? "" : "";
                _lblDetail.Text =
                    $"时间 {Get("时间")}    SN {Get("流水号")}    配方 {Get("配方")}    结果 {Get("结果")}    " +
                    $"偏差 X/Y {Get("X偏差mm")} / {Get("Y偏差mm")} mm（{Get("X偏差px")} / {Get("Y偏差px")} px）  得分 {Get("得分")}  角度 {Get("角度")}°  " +
                    $"面积 {Get("面积px²")} px²  表面 {Get("表面pct")}%  CT {Get("CTms")} ms  节拍 {Get("节拍ms")} ms  " +
                    $"原因 {Get("不良原因")}";
                _lblDetail.ForeColor = Get("结果") == "OK" ? Theme.Text : Theme.Ng;
            }
            catch { }
        }

        private void Export()
        {
            if (!Hub.IsInitialized) return;
            using var sfd = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = $"检测记录_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (sfd.ShowDialog() != DialogResult.OK) return;

            int n = Hub.Db.ExportCsv(sfd.FileName, BuildFilter());
            MessageBox.Show(FindForm(), n > 0 ? $"已导出 {n} 条记录到：\n{sfd.FileName}" : "没有可导出的数据，或导出失败（详见日志）。",
                "导出", MessageBoxButtons.OK, n > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void ClearAll()
        {
            if (!Auth.Current.Can(Permission.ClearDatabase))
            {
                MessageBox.Show(FindForm(), "仅管理员可清空历史数据。", "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(FindForm(), "将永久删除全部历史检测记录（不可恢复）。确认继续？", "危险操作",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (MessageBox.Show(FindForm(), "请再次确认：所有质量追溯数据都将丢失。", "二次确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Error) != DialogResult.Yes) return;

            if (Hub.Db.ClearAll())
            {
                Query();
                MessageBox.Show(FindForm(), "历史数据已清空。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
