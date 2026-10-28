using System.ComponentModel;
using System.Drawing.Drawing2D;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 深色滑块。WinForms 自带的 TrackBar 只在浅色主题下渲染，BackColor 对凹槽无效，
    /// 在深色界面里会露出一条刺眼的白底。这里用完全自绘替换，保证与主题一致。
    /// </summary>
    public sealed class DarkSlider : Control
    {
        private int _min;
        private int _max = 100;
        private int _value;
        private bool _dragging;

        public DarkSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Card;
            Height = Theme.LineHeight(Theme.F(9F)) + 12;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _min;
            set
            {
                if (_min == value) return;
                _min = value;
                // 先放大上限再夹取，否则"Minimum > 默认 Maximum"会抛 Math.Clamp 异常
                if (_max <= _min) _max = _min + 1;
                _value = Math.Clamp(_value, _min, _max);
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _max;
            set
            {
                _max = Math.Max(value, _min + 1);
                _value = Math.Clamp(_value, _min, _max);
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Clamp(value, _min, _max);
                if (v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? ValueChanged;

        private const int KnobR = 6;
        private int TrackLeft => KnobR + 2;
        private int TrackRight => Width - KnobR - 2;
        private int TrackY => Height / 2;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int x0 = TrackLeft, x1 = TrackRight;
            if (x1 <= x0) return;

            double ratio = _max > _min ? (Value - _min) / (double)(_max - _min) : 0;
            int kx = (int)Math.Round(x0 + ratio * (x1 - x0));

            // 凹槽
            using (var bg = new Pen(Color.FromArgb(44, 49, 58), 5F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(bg, x0, TrackY, x1, TrackY);
            // 已选区间
            if (kx > x0)
            {
                using var fg = new Pen(Theme.Accent, 5F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(fg, x0, TrackY, kx, TrackY);
            }

            // 手柄
            using (var glow = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                g.FillEllipse(glow, kx - KnobR - 3, TrackY - KnobR - 3, (KnobR + 3) * 2, (KnobR + 3) * 2);
            using (var knob = new SolidBrush(Color.White))
                g.FillEllipse(knob, kx - KnobR, TrackY - KnobR, KnobR * 2, KnobR * 2);
            using (var ring = new Pen(Theme.Accent, 2F))
                g.DrawEllipse(ring, kx - KnobR, TrackY - KnobR, KnobR * 2, KnobR * 2);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value += e.Delta > 0 ? 1 : -1;
        }

        private void SetFromX(int x)
        {
            int x0 = TrackLeft, x1 = TrackRight;
            if (x1 <= x0) return;
            double ratio = Math.Clamp((x - x0) / (double)(x1 - x0), 0, 1);
            Value = (int)Math.Round(_min + ratio * (_max - _min));
        }
    }
}
