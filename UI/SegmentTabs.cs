using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 深色分段页签，替代 WinForms 自带 TabControl。
    ///
    /// 为什么不用 TabControl：它的标签条背景由系统窗口过程绘制（不受 BackColor 控制，Paint 事件也盖不住），
    /// 在深色界面里永远露出一条浅色带。自绘一套反而更简单、也更符合工业 HMI 的观感。
    /// </summary>
    public sealed class SegmentTabs : Panel
    {
        private readonly FlowLayoutPanel _strip;
        private readonly Panel _body;
        private readonly List<(Button Btn, Control Page)> _pages = new();
        private int _selected = -1;

        public SegmentTabs()
        {
            BackColor = Theme.Bg;

            _body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            _strip = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 0,
                BackColor = Theme.Panel,
                Padding = new Padding(10, 8, 10, 0),
                WrapContents = false
            };
            _strip.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawLine(pen, 0, _strip.Height - 1, _strip.Width, _strip.Height - 1);
            };

            // Fill 先加，Top 后加（WinForms 按 z 逆序停靠）
            Controls.Add(_body);
            Controls.Add(_strip);
        }

        public int SelectedIndex => _selected;

        public void AddPage(string title, Control page)
        {
            var font = Theme.F(10F, FontStyle.Bold);
            var btn = new Button
            {
                Text = "  " + title + "  ",
                AutoSize = false,
                Width = TextRenderer.MeasureText("  " + title + "  ", font).Width + 28,
                Height = Theme.LineHeight(font) + 14,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Theme.SubText,
                Font = font,
                Cursor = Cursors.Hand,
                TabStop = false,
                Margin = new Padding(0, 0, 4, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 38, 46);

            int index = _pages.Count;
            btn.Click += (_, _) => Select(index);

            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _body.Controls.Add(page);
            _strip.Controls.Add(btn);
            _pages.Add((btn, page));

            _strip.Height = btn.Height + 10;
            if (_selected < 0) Select(0);
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _pages.Count) return;
            _selected = index;
            for (int i = 0; i < _pages.Count; i++)
            {
                bool active = i == index;
                _pages[i].Page.Visible = active;
                _pages[i].Btn.ForeColor = active ? Theme.Text : Theme.SubText;
                _pages[i].Btn.BackColor = active ? Theme.Bg : Color.Transparent;
                if (active) _pages[i].Page.BringToFront();
            }
        }
    }
}
