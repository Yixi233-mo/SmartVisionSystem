using System.Globalization;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SmartVisionSystem.Core;
using SmartVisionSystem.Infrastructure;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 参数与图纸（设备工程师界面）：
    /// ① 配方参数  ② 模板建立（框选特征 ROI）  ③ 相机与标定
    ///
    /// 关键修复：模板框选严格按 ImageMapper 的 Zoom 缩放系数换算到图像坐标，
    /// 初版直接减偏移量导致框出来的模板位置错位，匹配自然失败。
    /// </summary>
    public sealed class SettingsView : UserControl
    {
        private readonly record struct NumField(string Key, string Label, Func<Recipe, double> Get, Action<Recipe, double> Set, string Unit);

        private readonly List<NumField> _judgeFields = new()
        {
            new("MinArea", "材料面积下限", r => r.MinAreaPx, (r, v) => r.MinAreaPx = v, "px²"),
            new("MaxArea", "材料面积上限", r => r.MaxAreaPx, (r, v) => r.MaxAreaPx = v, "px²"),
            new("Score", "匹配得分阈值", r => r.MatchThreshold, (r, v) => r.MatchThreshold = v, "0~1"),
            new("Surface", "表面缺陷率上限", r => r.MaxSurfaceDefectRatio * 100, (r, v) => r.MaxSurfaceDefectRatio = v / 100, "%"),
        };

        private readonly List<NumField> _searchFields = new()
        {
            new("Window", "搜索窗口半径", r => r.SearchWindowPx, (r, v) => r.SearchWindowPx = (int)v, "px"),
            new("Angle", "旋转搜索范围 ±", r => r.SearchAngleDeg, (r, v) => r.SearchAngleDeg = v, "deg"),
            new("AngleStep", "旋转搜索步距", r => r.SearchAngleStep, (r, v) => r.SearchAngleStep = v, "deg"),
            new("PxPerMm", "像素当量", r => r.PixelsPerMm, (r, v) => r.PixelsPerMm = v, "px/mm"),
        };

        private readonly List<NumField> _robotFields = new()
        {
            new("BaseX", "机械手基准 X", r => r.RobotBaseX, (r, v) => r.RobotBaseX = v, "mm"),
            new("BaseY", "机械手基准 Y", r => r.RobotBaseY, (r, v) => r.RobotBaseY = v, "mm"),
            new("ZSafe", "安全高度 Z", r => r.ZSafe, (r, v) => r.ZSafe = v, "mm"),
            new("ZPick", "取料高度 Z", r => r.ZPick, (r, v) => r.ZPick = v, "mm"),
            new("ZPlace", "放料高度 Z", r => r.ZPlace, (r, v) => r.ZPlace = v, "mm"),
            new("PlaceX", "良品放料 X", r => r.PlaceX, (r, v) => r.PlaceX = v, "mm"),
            new("PlaceY", "良品放料 Y", r => r.PlaceY, (r, v) => r.PlaceY = v, "mm"),
            new("NgX", "NG 放料 X", r => r.PlaceNgX, (r, v) => r.PlaceNgX = v, "mm"),
            new("NgY", "NG 放料 Y", r => r.PlaceNgY, (r, v) => r.PlaceNgY = v, "mm"),
        };

        private readonly List<NumField> _cameraFields = new()
        {
            new("Exposure", "曝光时间", r => r.ExposureUs, (r, v) => r.ExposureUs = v, "μs"),
            new("Gain", "增益", r => r.GainDb, (r, v) => r.GainDb = v, "dB"),
            new("MaxCycle", "节拍上限", r => r.MaxCycleMs, (r, v) => r.MaxCycleMs = v, "ms"),
        };

        private readonly Dictionary<string, TextBox> _inputs = new();
        private Panel? _recipeScroll;
        private readonly List<Panel> _fitCards = new();
        private readonly List<(Panel Card, List<(Label Lbl, TextBox Tb, Label Unit)> Items)> _fieldCards = new();
        private readonly TextBox _txtName = Field.Input("", 220);
        private readonly Label _lblTemplateInfo = new();
        private readonly Label _lblValidate = new();
        private readonly ListBox _recipeList = new();

        private Recipe _draft = new();
        private Mat? _frame;

        private readonly VideoPanel _videoTpl = new() { Placeholder = "点击【从相机取图】或【生成标称样板】" };
        private readonly VideoPanel _videoCal = new() { Placeholder = "点击【实时预览】或【抓取单帧】" };
        private RectangleF? _selection;
        private bool _dragging;
        private PointF _dragStart;
        private PointF? _calPoint1, _calPoint2;
        private int _calStep;

        private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 120 };
        private readonly Label _lblPreviewStat = new();

        public SettingsView()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;

            var tabs = new SegmentTabs { Dock = DockStyle.Fill };
            tabs.AddPage("配方参数", BuildRecipePage());
            tabs.AddPage("模板与图纸", BuildTemplatePage());
            tabs.AddPage("相机与标定", BuildCalibrationPage());
            Controls.Add(tabs);

            LoadDraft(RecipeStore.Current);
            _previewTimer.Tick += (_, _) => PreviewTick();
        }

        private static DeviceHub Hub => DeviceHub.I;

        // ═══════════════════════ ① 配方参数 ═══════════════════════

        private Panel BuildRecipePage()
        {
            var page = new Panel { BackColor = Theme.Bg, Padding = new Padding(0) };

            var right = new Panel { Dock = DockStyle.Right, Width = 330, BackColor = Theme.Panel, Padding = new Padding(14) };

            // Fill 必须先加入，否则会被 Right 面板挤压
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            page.Controls.Add(scroll);
            page.Controls.Add(right);

            // 卡片宽度必须跟随容器：写死 596 时容器只有 ~530，第二列和单位会被裁掉
            _recipeScroll = scroll;
            scroll.Resize += (_, _) => FitCardWidths();

            int y = 6;

            // 产品信息
            var grpInfo = Section("产品信息", 116);
            _fitCards.Add(grpInfo);
            grpInfo.Location = new Point(0, y);
            y += 124;
            AddTextRow(grpInfo, "产品 / 配方名称", _txtName, 40);
            _lblTemplateInfo.Location = new Point(16, 76);
            _lblTemplateInfo.AutoSize = true;
            _lblTemplateInfo.ForeColor = Theme.Muted;
            _lblTemplateInfo.Font = Theme.F(8.5F);
            _lblTemplateInfo.BackColor = Color.Transparent;
            grpInfo.Controls.Add(_lblTemplateInfo);

            var grpJudge = BuildFieldGroup("判定标准（决定 OK / NG）", _judgeFields, ref y, 132);
            var grpSearch = BuildFieldGroup("匹配搜索策略（影响 CT 与鲁棒性）", _searchFields, ref y, 132);
            var grpRobot = BuildFieldGroup("机械手点位（图纸基准与放料位）", _robotFields, ref y, 282);
            var grpCam = BuildFieldGroup("相机与节拍", _cameraFields, ref y, 132);

            scroll.Controls.Add(grpRobot);
            scroll.Controls.Add(grpCam);
            scroll.Controls.Add(grpSearch);
            scroll.Controls.Add(grpJudge);
            scroll.Controls.Add(grpInfo);

            // 右侧：配方库 + 保存
            var lblLib = new Label
            {
                Text = "配方库",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Theme.SubText,
                Font = Theme.F(9F, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            _recipeList.Dock = DockStyle.Top;
            _recipeList.Height = 160;
            _recipeList.BackColor = Color.FromArgb(28, 31, 37);
            _recipeList.ForeColor = Theme.Text;
            _recipeList.BorderStyle = BorderStyle.FixedSingle;
            _recipeList.Font = Theme.F(9.5F);
            _recipeList.IntegralHeight = false;

            var btnRow1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
            var btnLoad = Theme.Btn("载入", Theme.Card, Theme.Text, 66, 30, 9F);
            var btnSave = Theme.Btn("保存", Theme.Ok, Color.White, 66, 30, 9F);
            var btnSaveAs = Theme.Btn("另存为", Theme.Card, Theme.Text, 74, 30, 9F);
            var btnDel = Theme.Btn("删除", Theme.Ng, Color.White, 66, 30, 9F);
            btnLoad.Click += (_, _) => DoLoad();
            btnSave.Click += (_, _) => DoSave(false);
            btnSaveAs.Click += (_, _) => DoSave(true);
            btnDel.Click += (_, _) => DoDelete();
            btnRow1.Controls.AddRange(new Control[] { btnLoad, btnSave, btnSaveAs, btnDel });

            var btnRow2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
            var btnApply = Theme.Btn("应用为当前生产配方", Theme.Accent, Color.White, 296, 32, 9.5F);
            btnApply.Click += (_, _) => DoSave(false);
            btnRow2.Controls.Add(btnApply);

            var btnRow3 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
            var btnLearn = Theme.Btn("按当前图像学习面积阈值", Theme.Card, Theme.Cyan, 296, 32, 9F);
            btnLearn.Click += (_, _) => DoLearnArea();
            btnRow3.Controls.Add(btnLearn);

            var btnRow4 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
            var btnRebuild = Theme.Btn("一键重建演示模板（恢复出厂）", Theme.Card, Theme.Warn, 296, 32, 9F);
            btnRebuild.Click += (_, _) => DoRebuildDemo();
            btnRow4.Controls.Add(btnRebuild);

            _lblValidate.Dock = DockStyle.Top;
            _lblValidate.Height = 90;
            _lblValidate.ForeColor = Theme.SubText;
            _lblValidate.Font = Theme.F(9F);
            _lblValidate.BackColor = Theme.Card;
            _lblValidate.Padding = new Padding(10);
            _lblValidate.Text = "—";

            // 先加 Fill，再按 由内到外 加 Top
            right.Controls.Add(_lblValidate);
            right.Controls.Add(btnRow4);
            right.Controls.Add(btnRow3);
            right.Controls.Add(btnRow2);
            right.Controls.Add(btnRow1);
            right.Controls.Add(_recipeList);
            right.Controls.Add(lblLib);

            return page;
        }

        private Panel Section(string title, int height, int width = 0)
        {
            var card = Theme.Card2(title, height);
            card.Dock = DockStyle.None;
            card.Width = width > 0 ? width : 596;
            return card;
        }

        private Panel BuildFieldGroup(string title, List<NumField> fields, ref int y, int minHeight)
        {
            int rowH = Math.Max(30, Theme.LineHeight(Theme.F(9F)) + 14);
            int rows = (fields.Count + 1) / 2;
            int height = Math.Max(minHeight, 42 + rows * rowH);

            var card = Section(title, height);
            card.Location = new Point(0, y);
            y += height + 8;

            var items = new List<(Label Lbl, TextBox Tb, Label Unit)>();
            foreach (var f in fields)
            {
                var lbl = Field.Label(f.Label);
                var tb = Field.Input(f.Get(_draft).ToString("0.####", CultureInfo.InvariantCulture), 78);
                var unit = new Label
                {
                    Text = f.Unit,
                    AutoSize = true,
                    ForeColor = Theme.Muted,
                    Font = Theme.Mono(8.5F),
                    BackColor = Color.Transparent
                };
                _inputs[f.Key] = tb;
                card.Controls.Add(lbl);
                card.Controls.Add(tb);
                card.Controls.Add(unit);
                items.Add((lbl, tb, unit));
            }

            _fieldCards.Add((card, items));
            _fitCards.Add(card);
            LayoutFields(card, items);
            return card;
        }

        /// <summary>按卡片实际宽度排两列：标签 / 输入框 / 单位依次串联，单位不会被挤出卡片右边界。</summary>
        private static void LayoutFields(Panel card, List<(Label Lbl, TextBox Tb, Label Unit)> items)
        {
            int rowH = Math.Max(30, Theme.LineHeight(Theme.F(9F)) + 14);
            int colW = Math.Max(180, (card.Width - 40) / 2);
            const int labelW = 130;

            for (int i = 0; i < items.Count; i++)
            {
                var (lbl, tb, unit) = items[i];
                int x = 14 + (i % 2) * colW;
                int rowY = 34 + (i / 2) * rowH;

                lbl.Location = new Point(x, rowY);
                lbl.Width = labelW;

                tb.Location = new Point(x + labelW, rowY - 2);
                tb.Width = Math.Max(64, colW - labelW - 42);

                unit.Location = new Point(tb.Right + 4, rowY + 1);
            }
        }

        /// <summary>卡片宽度跟随滚动区宽度，避免"卡片比容器宽 → 右侧整列被裁"。</summary>
        private void FitCardWidths()
        {
            if (_recipeScroll == null || _fieldCards.Count == 0) return;

            int avail = _recipeScroll.ClientSize.Width - 8;
            if (avail < 460) avail = 460;          // 太窄时保持最小可用宽度，交给横向滚动

            foreach (var card in _fitCards)
                if (card.Width != avail) card.Width = avail;

            foreach (var (card, items) in _fieldCards)
                LayoutFields(card, items);
        }

        private static void AddTextRow(Control parent, string label, TextBox box, int y, int x = 14)
        {
            var lbl = Field.Label(label);
            lbl.Location = new Point(x, y);
            lbl.Width = 150;
            box.Location = new Point(x + 150, y - 2);
            parent.Controls.Add(lbl);
            parent.Controls.Add(box);
        }

        private void LoadDraft(Recipe source)
        {
            // 注意：必须带模板一起克隆，否则 Clone() 会丢弃 Mat，界面会误报"未建立视觉模板"
            _draft?.Dispose();
            _draft = source.CloneWithTemplate();

            _txtName.Text = _draft.Name;
            foreach (var f in _judgeFields.Concat(_searchFields).Concat(_robotFields).Concat(_cameraFields))
            {
                if (_inputs.TryGetValue(f.Key, out var tb))
                    tb.Text = f.Get(_draft).ToString("0.####", CultureInfo.InvariantCulture);
            }

            _lblTemplateInfo.Text = _draft.HasTemplate
                ? $"模板：{_draft.Template!.Width}×{_draft.Template.Height}px   视觉基准：({_draft.ImageBaseX:F1}, {_draft.ImageBaseY:F1})"
                : "⚠ 当前配方尚未建立视觉模板，无法启动生产";

            RefreshRecipeList();
            ValidateDraft();
        }

        private void RefreshRecipeList()
        {
            _recipeList.Items.Clear();
            foreach (var name in RecipeStore.List()) _recipeList.Items.Add(name);
            _recipeList.SelectedItem = _draft.Name;
        }

        private bool ParseDraft()
        {
            foreach (var f in _judgeFields.Concat(_searchFields).Concat(_robotFields).Concat(_cameraFields))
            {
                if (!_inputs.TryGetValue(f.Key, out var tb)) continue;
                if (!double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    MessageBox.Show(FindForm(), $"参数【{f.Label}】不是合法数字：{tb.Text}", "参数校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                f.Set(_draft, v);
            }

            _draft.Name = _txtName.Text.Trim();
            return true;
        }

        private void ValidateDraft()
        {
            if (!_draft.HasTemplate)
            {
                _lblValidate.ForeColor = Theme.Warn;
                _lblValidate.Text = "配方校验：未建立视觉模板\n请在【模板与图纸】页框选特征并建立模板后再保存。";
                return;
            }
            _lblValidate.ForeColor = _draft.Validate(out var err) ? Theme.Ok : Theme.Ng;
            _lblValidate.Text = _draft.Validate(out _)
                ? $"配方校验通过\n模板 {_draft.Template!.Width}×{_draft.Template.Height}px\n面积 {_draft.MinAreaPx:F0}~{_draft.MaxAreaPx:F0} px²\n节拍上限 {_draft.MaxCycleMs:F0} ms"
                : "配方校验失败：" + err;
        }

        private bool GuardRunning()
        {
            if (Hub.Station.State is not (StationState.Running or StationState.Homing)) return true;
            MessageBox.Show(FindForm(), "生产运行中禁止修改配方参数。\n请先停止生产（参数变更会影响正在判定的产品，存在批量误判风险）。",
                "防呆拦截", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Log.Warn("配方", "运行中尝试修改配方，已拦截");
            return false;
        }

        private void DoSave(bool saveAs)
        {
            if (!GuardRunning()) return;
            if (!ParseDraft()) return;

            if (saveAs)
            {
                var name = PromptInput("另存为新的配方名称：", _draft.Name + "-副本");
                if (string.IsNullOrWhiteSpace(name)) return;
                _draft.Name = name.Trim();
            }

            // 模板 Mat 深拷贝，避免"退休"的旧配方与当前配方共用同一块内存
            _draft.Template?.Dispose();
            _draft.TemplateGray?.Dispose();
            _draft.Template = RecipeStore.Current.Template?.Clone();
            _draft.TemplateGray = RecipeStore.Current.TemplateGray?.Clone();
            if (!_draft.HasTemplate && _frame != null && _selection.HasValue)
                BuildTemplateFromSelection(silent: true);

            if (!_draft.Validate(out var err))
            {
                MessageBox.Show(FindForm(), "无法保存：\n" + err, "配方校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!RecipeStore.Save(_draft))
            {
                MessageBox.Show(FindForm(), "保存失败，详见日志。", "配方", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RecipeStore.Apply(_draft);
            Hub.Camera.ExposureUs = _draft.ExposureUs;
            Hub.Camera.GainDb = _draft.GainDb;
            Hub.Camera.PixelsPerMm = _draft.PixelsPerMm;
            Hub.ApplySettings();

            LoadDraft(_draft);
            MessageBox.Show(FindForm(), $"配方【{_draft.Name}】已保存并应用。", "配方", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoLoad()
        {
            if (!GuardRunning()) return;
            if (_recipeList.SelectedItem is not string name) return;
            var recipe = RecipeStore.Load(name, makeCurrent: false);
            if (recipe == null)
            {
                MessageBox.Show(FindForm(), $"配方 {name} 加载失败（可能模板文件缺失）。", "载入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            LoadDraft(recipe);
            Log.Info("配方", $"载入配方 {name} 到编辑区");
        }

        private void DoDelete()
        {
            if (!Auth.Current.Can(Permission.EditRecipe)) return;
            if (_recipeList.SelectedItem is not string name) return;

            if (RecipeStore.List().Count <= 1)
            {
                MessageBox.Show(FindForm(), "至少保留一个配方，无法删除最后一个。", "删除配方", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(FindForm(), $"确认删除配方【{name}】？此操作不可撤销。", "删除确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            RecipeStore.Delete(name);
            RefreshRecipeList();
        }

        /// <summary>用当前相机画面学习材料面积上下限（±3.5%）</summary>
        private void DoLearnArea()
        {
            if (!GuardRunning()) return;
            if (_frame == null)
            {
                MessageBox.Show(FindForm(), "请先在【模板与图纸】页抓取一帧图像。", "学习面积", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var recipe = _draft;
            if (!recipe.HasTemplate)
            {
                MessageBox.Show(FindForm(), "请先建立模板。", "学习面积", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = VisionEngine.Inspect(_frame, recipe);
            if (result.MaterialAreaPx <= 0)
            {
                MessageBox.Show(FindForm(), "面积测量失败：请确认画面中工件清晰可见，或先调整模板。", "学习面积", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetInput("MinArea", result.MaterialAreaPx * 0.965);
            SetInput("MaxArea", result.MaterialAreaPx * 1.035);
            SetInput("Surface", Math.Max(0.4, result.SurfaceRatio * 100 * 3));
            ParseDraft();
            ValidateDraft();
            Log.Info("配方", $"按当前图像学习：材料面积 {result.MaterialAreaPx:F0}px²，表面缺陷基准 {result.SurfaceRatio * 100:F3}%");
            MessageBox.Show(FindForm(), $"已按当前图像学习阈值：\n材料面积 {result.MaterialAreaPx:F0} px²（上下限 ±3.5%）\n表面缺陷率基准 {result.SurfaceRatio * 100:F3}%", "学习完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetInput(string key, double value)
        {
            if (_inputs.TryGetValue(key, out var tb)) tb.Text = value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void DoRebuildDemo()
        {
            if (!GuardRunning()) return;
            if (MessageBox.Show(FindForm(), "将根据虚拟标称件重新生成模板与阈值，并覆盖当前演示配方。继续？", "重建模板",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            if (DemoRecipeFactory.EnsureDemo(Hub.Scene, RecipeStore.Current.PixelsPerMm))
            {
                LoadDraft(RecipeStore.Current);
                MessageBox.Show(FindForm(), "演示模板已重建。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else MessageBox.Show(FindForm(), "重建失败，详见日志。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ═══════════════════════ ② 模板与图纸 ═══════════════════════

        private Panel BuildTemplatePage()
        {
            var page = new Panel { BackColor = Theme.Bg, Padding = new Padding(0) };

            var bar = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Panel, Padding = new Padding(10, 8, 10, 8) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent };

            var btnFromCam = Theme.Btn("从相机取图", Theme.Accent, Color.White, 110, 34, 9F);
            var btnNominal = Theme.Btn("生成标称样板", Theme.Card, Theme.Cyan, 120, 34, 9F);
            var btnLoadImg = Theme.Btn("载入图纸文件", Theme.Card, Theme.Text, 120, 34, 9F);
            var btnAuto = Theme.Btn("自动框选工件", Theme.Card, Theme.Text, 120, 34, 9F);
            var btnBuild = Theme.Btn("建立模板", Theme.Ok, Color.White, 110, 34, 9F);
            var btnTest = Theme.Btn("校验匹配", Theme.Card, Theme.Warn, 110, 34, 9F);

            btnFromCam.Click += (_, _) => CaptureFromCamera();
            btnNominal.Click += (_, _) => CaptureNominal();
            btnLoadImg.Click += (_, _) => LoadDrawingFile();
            btnAuto.Click += (_, _) => AutoSelect();
            btnBuild.Click += (_, _) => BuildTemplateFromSelection(silent: false);
            btnTest.Click += (_, _) => TestMatch();

            foreach (var b in new[] { btnFromCam, btnNominal, btnLoadImg, btnAuto, btnBuild, btnTest })
            {
                b.Margin = new Padding(0, 0, 8, 0);
                flow.Controls.Add(b);
            }

            bar.Controls.Add(flow);

            _videoTpl.Dock = DockStyle.Fill;
            _videoTpl.Cursor = Cursors.Cross;
            _videoTpl.MouseDown += Video_MouseDown;
            _videoTpl.MouseMove += Video_MouseMove;
            _videoTpl.MouseUp += Video_MouseUp;

            var tip = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                ForeColor = Theme.Muted,
                Font = Theme.F(9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                Text = "操作：先在画面上拖拽框选工件特征（尽量贴紧工件本体）→ 点【建立模板】。模板与判定基准点自动记录，随后可在【配方参数】页学习面积阈值。",
                BackColor = Theme.Panel
            };
            page.Controls.Add(_videoTpl);
            page.Controls.Add(bar);
            page.Controls.Add(tip);

            return page;
        }

        private void CaptureFromCamera()
        {
            try
            {
                Hub.Camera.PixelsPerMm = _draft.PixelsPerMm;
                using var mat = Hub.Camera.GrabFrame();
                SetFrame(mat, _videoTpl);
                _selection = null;
                Log.Info("配方", "已从相机抓取一帧用于建立模板");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "取图失败：" + ex.Message, "相机", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CaptureNominal()
        {
            using var mat = Hub.Scene.RenderNominal(_draft.PixelsPerMm);
            SetFrame(mat, _videoTpl);
            _selection = null;
            AutoSelect();
            Log.Info("配方", "已生成标称样板图像（零偏差、无缺陷）");
        }

        private void LoadDrawingFile()
        {
            using var ofd = new OpenFileDialog { Filter = "图像文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif|所有文件|*.*" };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            try
            {
                var bytes = File.ReadAllBytes(ofd.FileName);
                using var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
                if (mat.Empty())
                {
                    MessageBox.Show(FindForm(), "图像解码失败。", "载入图纸", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SetFrame(mat, _videoTpl);
                _selection = null;
                Log.Info("配方", $"载入图纸文件：{Path.GetFileName(ofd.FileName)}（{mat.Width}×{mat.Height}）");
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "载入失败：" + ex.Message, "载入图纸", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AutoSelect()
        {
            if (_frame == null) return;
            if (!VisionEngine.AutoDetectPart(_frame, out var rect, out _))
            {
                MessageBox.Show(FindForm(), "未能自动定位工件（画面中对比度不足）。请手动框选。", "自动框选", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _selection = new RectangleF(rect.X - 8, rect.Y - 8, rect.Width + 16, rect.Height + 16);
            _videoTpl.SelectionRect = _selection;
            _videoTpl.Invalidate();
            Log.Info("配方", $"自动框选工件：{rect.Width}×{rect.Height}px @({rect.X},{rect.Y})");
        }

        private void BuildTemplateFromSelection(bool silent)
        {
            if (!GuardRunning()) return;
            if (_frame == null || !_selection.HasValue)
            {
                if (!silent) MessageBox.Show(FindForm(), "请先在画面上框选工件特征区域。", "建立模板", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sel = _selection.Value;
            int x = (int)Math.Round(sel.X), y = (int)Math.Round(sel.Y);
            int w = (int)Math.Round(sel.Width), h = (int)Math.Round(sel.Height);
            x = Math.Clamp(x, 0, _frame.Width - 1);
            y = Math.Clamp(y, 0, _frame.Height - 1);
            w = Math.Clamp(w, 8, _frame.Width - x);
            h = Math.Clamp(h, 8, _frame.Height - y);

            using var gray = new Mat();
            Cv2.CvtColor(_frame, gray, ColorConversionCodes.BGR2GRAY);
            using var view = new Mat(gray, new Rect(x, y, w, h));

            _draft.Template?.Dispose();
            _draft.TemplateGray?.Dispose();
            _draft.Template = view.Clone();
            _draft.TemplateGray = view.Clone();

            // 视觉基准 = 模板中心：来料偏差由此计算
            _draft.ImageBaseX = x + w / 2.0;
            _draft.ImageBaseY = y + h / 2.0;

            _lblTemplateInfo.Text = $"模板：{w}×{h}px   视觉基准：({_draft.ImageBaseX:F1}, {_draft.ImageBaseY:F1})";

            // 立即用新模板测一次面积，给出建议阈值
            var probe = VisionEngine.Inspect(_frame, _draft);
            if (probe.MaterialAreaPx > 0)
            {
                SetInput("MinArea", probe.MaterialAreaPx * 0.965);
                SetInput("MaxArea", probe.MaterialAreaPx * 1.035);
                ParseDraft();
            }

            ValidateDraft();
            Log.Info("配方", $"建立模板成功：{w}×{h}px，视觉基准 ({_draft.ImageBaseX:F1},{_draft.ImageBaseY:F1})，实测得分 {probe.Score:F3}");

            if (!silent)
                MessageBox.Show(FindForm(),
                    $"模板建立成功：{w} × {h} px\n视觉基准点：({_draft.ImageBaseX:F0}, {_draft.ImageBaseY:F0})\n" +
                    $"实测匹配得分：{probe.Score:F3}（阈值 {_draft.MatchThreshold:F2}）\n\n" +
                    "已按实测面积给出建议阈值，请到【配方参数】页保存配方。",
                    "建立模板", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void TestMatch()
        {
            if (_frame == null || !_draft.HasTemplate)
            {
                MessageBox.Show(FindForm(), "请先取图并建立模板。", "校验匹配", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = VisionEngine.Inspect(_frame, _draft);
            _videoTpl.SetFrame(BitmapConverter.ToBitmap(_frame), result, _draft);
            Log.Info("配方", $"校验匹配：{(result.IsOk ? "OK" : "NG")} 得分 {result.Score:F3} 偏差({result.OffsetXmm:+0.00;-0.00},{result.OffsetYmm:+0.00;-0.00})mm CT {result.CtMs:F1}ms");
            MessageBox.Show(FindForm(),
                $"判定：{(result.IsOk ? "OK" : "NG — " + result.NgReason)}\n" +
                $"匹配得分：{result.Score:F3}\n旋转角度：{result.AngleDeg:+0.0;-0.0}°\n" +
                $"偏差：X {result.OffsetXmm:+0.000;-0.000} mm / Y {result.OffsetYmm:+0.000;-0.000} mm\n" +
                $"材料面积：{result.MaterialAreaPx:F0} px²\n表面缺陷率：{result.SurfaceRatio * 100:F2}%\n" +
                $"视觉耗时：{result.CtMs:F1} ms",
                "校验匹配", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetFrame(Mat mat, VideoPanel panel)
        {
            _frame?.Dispose();
            _frame = mat.Clone();
            panel.SetFrame(BitmapConverter.ToBitmap(mat), null, null);
        }

        private void Video_MouseDown(object? sender, MouseEventArgs e)
        {
            if (_frame == null || e.Button != MouseButtons.Left) return;
            var p = _videoTpl.Mapper.ToImage(e.X, e.Y);
            _dragStart = p;
            _dragging = true;
            _selection = new RectangleF(p.X, p.Y, 0, 0);
            _videoTpl.SelectionRect = _selection;
        }

        private void Video_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_dragging || _frame == null) return;
            var p = _videoTpl.Mapper.ToImage(e.X, e.Y);
            float x = Math.Min(_dragStart.X, p.X);
            float y = Math.Min(_dragStart.Y, p.Y);
            float w = Math.Abs(p.X - _dragStart.X);
            float h = Math.Abs(p.Y - _dragStart.Y);
            _selection = new RectangleF(x, y, w, h);
            _videoTpl.SelectionRect = _selection;
            _videoTpl.Invalidate();
        }

        private void Video_MouseUp(object? sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            if (_selection.HasValue && (_selection.Value.Width < 8 || _selection.Value.Height < 8))
            {
                _selection = null;
                _videoTpl.SelectionRect = null;
                _videoTpl.Invalidate();
                return;
            }
            if (_selection.HasValue)
                Log.Info("配方", $"框选 ROI：{_selection.Value.Width:F0}×{_selection.Value.Height:F0} @({_selection.Value.X:F0},{_selection.Value.Y:F0})");
        }

        // ═══════════════════════ ③ 相机与标定 ═══════════════════════

        private Panel BuildCalibrationPage()
        {
            var page = new Panel { BackColor = Theme.Bg, Padding = new Padding(0) };

            var side = new Panel { Dock = DockStyle.Right, Width = 340, BackColor = Theme.Panel, Padding = new Padding(14) };

            var bar = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Panel, Padding = new Padding(10, 8, 10, 8) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent };
            var btnLive = Theme.Btn("▶ 实时预览", Theme.Accent, Color.White, 116, 34, 9F);
            var btnSnap = Theme.Btn("抓取单帧", Theme.Card, Theme.Text, 100, 34, 9F);
            btnLive.Click += (_, _) => ToggleLive();
            btnSnap.Click += (_, _) => { StopLive(); CaptureForCal(); };
            flow.Controls.AddRange(new Control[] { btnLive, btnSnap });

            _lblPreviewStat.AutoSize = false;
            _lblPreviewStat.Width = 320;
            _lblPreviewStat.Height = 34;
            _lblPreviewStat.ForeColor = Theme.Cyan;
            _lblPreviewStat.Font = Theme.Mono(9F);
            _lblPreviewStat.TextAlign = ContentAlignment.MiddleLeft;
            _lblPreviewStat.Dock = DockStyle.Right;
            flow.Controls.Add(_lblPreviewStat);
            bar.Controls.Add(flow);

            _videoCal.Dock = DockStyle.Fill;
            _videoCal.Cursor = Cursors.Cross;
            _videoCal.MouseDown += Cal_MouseDown;
            page.Controls.Add(_videoCal);
            page.Controls.Add(side);
            page.Controls.Add(bar);

            int y = 34;
            var grpCam = Theme.Card2("相机成像参数", 150);
            grpCam.Dock = DockStyle.None;
            grpCam.Width = 312;
            grpCam.Location = new Point(0, y);
            y += 158;

            var lblExp = Field.Label("曝光 (μs)");
            lblExp.Location = new Point(14, 38);
            lblExp.Width = 90;
            var tbExp = Field.Input(_draft.ExposureUs.ToString("F0"), 90);
            tbExp.Location = new Point(110, 36);
            tbExp.TextChanged += (_, _) =>
            {
                if (double.TryParse(tbExp.Text, out var v)) { Hub.Camera.ExposureUs = v; SetInput("Exposure", v); _draft.ExposureUs = v; }
            };

            var lblGain = Field.Label("增益 (dB)");
            lblGain.Location = new Point(14, 76);
            lblGain.Width = 90;
            var tbGain = Field.Input(_draft.GainDb.ToString("F0"), 90);
            tbGain.Location = new Point(110, 74);
            tbGain.TextChanged += (_, _) =>
            {
                if (double.TryParse(tbGain.Text, out var v)) { Hub.Camera.GainDb = v; SetInput("Gain", v); _draft.GainDb = v; }
            };

            var lblNote = new Label
            {
                Text = "曝光/增益会实时影响画面与算法得分，\n建议在保证不饱和的前提下尽量提高对比度。",
                Location = new Point(14, 104),
                Width = 284,
                Height = 40,
                ForeColor = Theme.Muted,
                Font = Theme.F(8.5F),
                BackColor = Color.Transparent
            };
            grpCam.Controls.AddRange(new Control[] { lblExp, tbExp, lblGain, tbGain, lblNote });

            var grpCal = Theme.Card2("像素当量标定（两点法）", 236);
            grpCal.Dock = DockStyle.None;
            grpCal.Width = 312;
            grpCal.Location = new Point(0, y);
            y += 244;

            var lblPx = Field.Label("当前像素当量");
            lblPx.Location = new Point(14, 38);
            lblPx.Width = 110;
            var tbPx = Field.Input(_draft.PixelsPerMm.ToString("0.###"), 80);
            tbPx.Location = new Point(130, 36);
            tbPx.TextChanged += (_, _) =>
            {
                if (double.TryParse(tbPx.Text, out var v) && v > 0.01) { SetInput("PxPerMm", v); _draft.PixelsPerMm = v; }
            };

            var btnCal1 = Theme.Btn("① 在画面点击两点", Theme.Card, Theme.Cyan, 284, 32, 9F);
            btnCal1.Location = new Point(14, 70);
            btnCal1.Click += (_, _) => { _calStep = 1; _calPoint1 = _calPoint2 = null; _lblPreviewStat.Text = "请点击第 1 点…"; };

            var lblDistance = Field.Label("实际距离 (mm)");
            lblDistance.Location = new Point(14, 112);
            lblDistance.Width = 110;
            var tbDistance = Field.Input("100", 80);
            tbDistance.Location = new Point(130, 110);

            var btnCal2 = Theme.Btn("② 计算并应用像素当量", Theme.Ok, Color.White, 284, 32, 9F);
            btnCal2.Location = new Point(14, 144);
            btnCal2.Click += (_, _) => ApplyCalibration(tbDistance, tbPx);

            var lblCalTip = new Label
            {
                Text = "两点法：在画面中量取已知距离（如标准块 100mm）\n两端像素距离，即可反算 px/mm。",
                Location = new Point(14, 184),
                Width = 284,
                Height = 44,
                ForeColor = Theme.Muted,
                Font = Theme.F(8.5F),
                BackColor = Color.Transparent
            };
            grpCal.Controls.AddRange(new Control[] { lblPx, tbPx, btnCal1, lblDistance, tbDistance, btnCal2, lblCalTip });

            side.Controls.Add(grpCal);
            side.Controls.Add(grpCam);

            return page;
        }

        private void ToggleLive()
        {
            if (_previewTimer.Enabled) StopLive();
            else
            {
                Hub.Camera.ExposureUs = _draft.ExposureUs;
                Hub.Camera.GainDb = _draft.GainDb;
                Hub.Camera.PixelsPerMm = _draft.PixelsPerMm;
                _previewTimer.Start();
                Log.Info("相机", "打开实时预览");
            }
        }

        private void StopLive()
        {
            if (!_previewTimer.Enabled) return;
            _previewTimer.Stop();
            Log.Info("相机", "关闭实时预览");
        }

        private void PreviewTick()
        {
            try
            {
                using var mat = Hub.Camera.GrabFrame();
                _lblPreviewStat.Text = $"曝光 {Hub.Camera.ExposureUs:F0}μs  增益 {Hub.Camera.GainDb:F0}dB";
                _videoCal.SetFrame(BitmapConverter.ToBitmap(mat), null, null);
            }
            catch (Exception ex)
            {
                StopLive();
                _lblPreviewStat.Text = "预览中断：" + ex.Message;
            }
        }

        private void CaptureForCal()
        {
            try
            {
                using var mat = Hub.Camera.GrabFrame();
                SetFrame(mat, _videoCal);
                _lblPreviewStat.Text = "已抓取单帧";
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "抓图失败：" + ex.Message, "相机", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Cal_MouseDown(object? sender, MouseEventArgs e)
        {
            if (_calStep == 0) return;
            var p = _videoCal.Mapper.ToImage(e.X, e.Y);
            if (_calStep == 1)
            {
                _calPoint1 = p;
                _calStep = 2;
                _lblPreviewStat.Text = "请点击第 2 点…";
                return;
            }
            if (_calStep == 2 && _calPoint1.HasValue)
            {
                _calPoint2 = p;
                _calStep = 3;
                double d = Math.Sqrt(Math.Pow(p.X - _calPoint1.Value.X, 2) + Math.Pow(p.Y - _calPoint1.Value.Y, 2));
                _lblPreviewStat.Text = $"两点像素距离 {d:F1} px，请输入实际 mm 后计算";
                _videoCal.SelectionRect = new RectangleF(
                    (float)Math.Min(_calPoint1.Value.X, p.X), (float)Math.Min(_calPoint1.Value.Y, p.Y),
                    (float)Math.Abs(p.X - _calPoint1.Value.X), (float)Math.Abs(p.Y - _calPoint1.Value.Y));
                _videoCal.Invalidate();
            }
        }

        private void ApplyCalibration(TextBox tbDistance, TextBox tbPx)
        {
            if (!_calPoint1.HasValue || !_calPoint2.HasValue)
            {
                MessageBox.Show(FindForm(), "请先在画面点击两个标定点。", "标定", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!double.TryParse(tbDistance.Text.Trim(), out var mm) || mm <= 0)
            {
                MessageBox.Show(FindForm(), "请输入有效的实际距离（mm）。", "标定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            double px = Math.Sqrt(Math.Pow(_calPoint2.Value.X - _calPoint1.Value.X, 2) + Math.Pow(_calPoint2.Value.Y - _calPoint1.Value.Y, 2));
            double pxPerMm = px / mm;
            if (pxPerMm < 0.05 || pxPerMm > 200)
            {
                MessageBox.Show(FindForm(), $"计算结果异常：{pxPerMm:F3} px/mm，请检查点位或距离。", "标定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            tbPx.Text = pxPerMm.ToString("0.###");
            SetInput("PxPerMm", pxPerMm);
            _draft.PixelsPerMm = pxPerMm;
            Log.Info("标定", $"两点标定完成：{px:F1}px = {mm:F2}mm → {pxPerMm:F3} px/mm");

            MessageBox.Show(FindForm(),
                $"标定完成：\n像素距离 {px:F1} px\n实际距离 {mm:F2} mm\n像素当量 {pxPerMm:F3} px/mm\n\n" +
                "注意：修改像素当量后，原模板尺度会与新成像不匹配，建议重新建立模板！",
                "标定", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ═══════════════════════ 通用 ═══════════════════════

        private string? PromptInput(string title, string defaultValue)
        {
            using var form = new Form
            {
                Text = title,
                ClientSize = new Size(380, 130),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Theme.Panel,
                Font = Theme.F(9.5F)
            };
            var tb = Field.Input(defaultValue, 330);
            tb.Location = new Point(22, 26);
            var ok = Theme.Btn("确定", Theme.Ok, Color.White, 120, 32, 9.5F);
            ok.Location = new Point(22, 72);
            ok.DialogResult = DialogResult.OK;
            var cancel = Theme.Btn("取消", Theme.Card, Theme.Text, 120, 32, 9.5F);
            cancel.Location = new Point(156, 72);
            cancel.DialogResult = DialogResult.Cancel;
            form.Controls.AddRange(new Control[] { tb, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            return form.ShowDialog(FindForm()) == DialogResult.OK ? tb.Text.Trim() : null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _previewTimer.Stop();
                _previewTimer.Dispose();
                _frame?.Dispose();
                _frame = null;
            }
            base.Dispose(disposing);
        }
    }
}
