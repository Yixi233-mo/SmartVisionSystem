using OpenCvSharp;
using SmartVisionSystem.Infrastructure;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace SmartVisionSystem.Simulation
{
    /// <summary>
    /// 虚拟视觉场景：把"虚拟产线上的工件"实时渲染成一张相机图像（BGR Mat）。
    ///
    /// 为什么需要它：没有真实相机时，视觉算法必须有像素输入才能验证。该渲染器刻意还原了工业现场的
    /// 几个关键干扰因素——皮带纹理随线速滚动、曝光/增益引起的亮度与噪声变化、来料平移与旋转、
    /// 以及可注入的四类物理缺陷，从而让"识别-判定-纠偏"闭环可以在纯电脑环境里被完整地压力验证。
    /// </summary>
    public sealed class VirtualScene : IDisposable
    {
        private sealed record Sprite(Mat Bgr, Mat Mask, int Canvas);

        private readonly int _w, _h;
        private readonly int _beltTop, _beltHeight;
        private readonly Mat _background;
        private readonly Mat[] _noise;
        private readonly Dictionary<string, Sprite> _sprites = new();
        private readonly object _sync = new();
        private int _noiseIdx;

        // 皮带纹理参数
        private const int SlatPeriodPx = 48;
        private const int SlatWidthPx = 5;

        public int Width => _w;
        public int Height => _h;

        /// <summary>工件外形尺寸（mm），由仿真参数驱动</summary>
        public double PartSizeMm { get; set; } = 40;

        public VirtualScene(int width = 1280, int height = 960)
        {
            _w = width;
            _h = height;
            _beltTop = (int)(height * 0.30);
            _beltHeight = (int)(height * 0.40);

            _background = BuildBackground();

            _noise = new Mat[3];
            for (int i = 0; i < _noise.Length; i++)
            {
                _noise[i] = new Mat(height, width, MatType.CV_8UC3);
                Cv2.Randu(_noise[i], new Scalar(116, 116, 116), new Scalar(140, 140, 140));
            }
        }

        private Mat BuildBackground()
        {
            var img = new Mat(_h, _w, MatType.CV_8UC3, new Scalar(24, 26, 30));

            // 机型机架（上下暗部）
            Cv2.Rectangle(img, new Rect(0, 0, _w, _beltTop), new Scalar(30, 32, 37), -1);
            Cv2.Rectangle(img, new Rect(0, _beltTop + _beltHeight, _w, _h - _beltTop - _beltHeight), new Scalar(30, 32, 37), -1);

            // 皮带带面
            Cv2.Rectangle(img, new Rect(0, _beltTop, _w, _beltHeight), new Scalar(44, 46, 50), -1);

            // 两侧导轨
            Cv2.Rectangle(img, new Rect(0, _beltTop - 12, _w, 12), new Scalar(62, 65, 72), -1);
            Cv2.Rectangle(img, new Rect(0, _beltTop + _beltHeight, _w, 12), new Scalar(62, 65, 72), -1);
            Cv2.Line(img, new Point(0, _beltTop - 12), new Point(_w, _beltTop - 12), new Scalar(84, 88, 96), 1);
            Cv2.Line(img, new Point(0, _beltTop + _beltHeight), new Point(_w, _beltTop + _beltHeight), new Scalar(84, 88, 96), 1);

            // 机架上的一些固定结构（让画面不那么"干净"，更贴近现场）
            Cv2.Rectangle(img, new Rect(60, 70, 220, 90), new Scalar(38, 41, 47), -1);
            Cv2.Rectangle(img, new Rect(_w - 300, _h - 170, 240, 100), new Scalar(38, 41, 47), -1);
            return img;
        }

        /// <summary>渲染当前产线状态（工位线程/UI 均可调用，内部串行化）</summary>
        public Mat Render(IReadOnlyList<LinePart> parts, double pxPerMm, double stationMm, double beltPhaseMm,
                          double exposureUs, double gainDb)
        {
            lock (_sync)
            {
                var img = _background.Clone();
                DrawSlats(img, pxPerMm, beltPhaseMm);
                foreach (var p in parts)
                    DrawPart(img, p.Defect, p.Seed, CenterX(p, pxPerMm, stationMm), _h / 2.0 + p.OffsetYPx, p.AngleDeg, pxPerMm);
                ApplyLighting(img, exposureUs, gainDb);
                return img;
            }
        }

        /// <summary>渲染"标称件"（零偏差、无缺陷、恰好在视野中心）——用于建立视觉基准模板</summary>
        public Mat RenderNominal(double pxPerMm, double exposureUs = 8000, double gainDb = 0)
            => RenderSinglePart(0, 0, 0, DefectKind.None, pxPerMm, exposureUs, gainDb);

        /// <summary>
        /// 渲染单个指定状态的工件（可指定偏差/角度/缺陷）——用于无产线条件下的算法自检与压力测试。
        /// </summary>
        public Mat RenderSinglePart(double offsetXPx, double offsetYPx, double angleDeg, DefectKind kind,
                                    double pxPerMm, double exposureUs = 8000, double gainDb = 0, int seed = 7)
        {
            lock (_sync)
            {
                var img = _background.Clone();
                DrawSlats(img, pxPerMm, 0);
                DrawPart(img, kind, seed, _w / 2.0 + offsetXPx, _h / 2.0 + offsetYPx, angleDeg, pxPerMm);
                ApplyLighting(img, exposureUs, gainDb);
                return img;
            }
        }

        /// <summary>工件在图像中的横坐标</summary>
        public double CenterX(LinePart part, double pxPerMm, double stationMm)
            => _w / 2.0 + (part.PositionMm - stationMm) * pxPerMm + part.OffsetXPx;

        // ─────────────────────────── 绘制 ───────────────────────────

        private void DrawSlats(Mat img, double pxPerMm, double beltPhaseMm)
        {
            double offset = (beltPhaseMm * pxPerMm) % SlatPeriodPx;
            int y0 = _beltTop + 2;
            int h = _beltHeight - 4;

            for (double x = -SlatPeriodPx + offset; x < _w; x += SlatPeriodPx)
            {
                int xi = (int)x;
                Cv2.Rectangle(img, new Rect(xi, y0, SlatWidthPx, h), new Scalar(52, 54, 59), -1);
                Cv2.Line(img, new Point(xi, y0), new Point(xi, y0 + h), new Scalar(36, 38, 42), 1);
            }

            // 皮带接缝：每 8 个纹理节距一条醒目缝线，肉眼可判断皮带是否在动
            for (double x = -SlatPeriodPx * 8 + offset; x < _w; x += SlatPeriodPx * 8)
            {
                int xi = (int)x;
                if (xi < -4 || xi >= _w) continue;
                Cv2.Rectangle(img, new Rect(xi, y0, 3, h), new Scalar(78, 82, 90), -1);
            }
        }

        private void DrawPart(Mat img, DefectKind kind, int seed, double cx, double cy, double angleDeg, double pxPerMm)
        {
            var sprite = GetSprite(kind, (int)Math.Round(PartSizeMm * pxPerMm), seed);
            int s = sprite.Canvas;
            double exactX = cx - s / 2.0, exactY = cy - s / 2.0;
            int x = (int)Math.Floor(exactX);
            int y = (int)Math.Floor(exactY);
            // 亚像素落位：整数像素落位会引入 ±0.5px 的量化误差，掩盖算法真实精度
            float fx = (float)(exactX - x), fy = (float)(exactY - y);
            if (x + s <= 0 || x >= _w || y + s <= 0 || y >= _h) return;

            using var rotBgr = new Mat();
            using var rotMask = new Mat();
            using var m = Cv2.GetRotationMatrix2D(new Point2f(s / 2f, s / 2f), angleDeg, 1.0);
            m.Set(0, 2, m.At<double>(0, 2) + fx);
            m.Set(1, 2, m.At<double>(1, 2) + fy);
            Cv2.WarpAffine(sprite.Bgr, rotBgr, m, new Size(s, s), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(0, 0, 0));
            Cv2.WarpAffine(sprite.Mask, rotMask, m, new Size(s, s), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(0));

            int rx = Math.Max(0, x), ry = Math.Max(0, y);
            int rw = Math.Min(_w - rx, x + s - rx), rh = Math.Min(_h - ry, y + s - ry);
            if (rw <= 0 || rh <= 0) return;

            var srcRoi = new Rect(rx - x, ry - y, rw, rh);
            using var srcBgr = new Mat(rotBgr, srcRoi);
            using var srcMask = new Mat(rotMask, srcRoi);
            using var dstRoi = new Mat(img, new Rect(rx, ry, rw, rh));
            // 掩膜只区分"工件本体"，避免旋转留下的黑角污染背景
            using var maskBin = new Mat();
            Cv2.Threshold(srcMask, maskBin, 128, 255, ThresholdTypes.Binary);
            srcBgr.CopyTo(dstRoi, maskBin);
        }

        private Sprite GetSprite(DefectKind kind, int partPx, int seed)
        {
            partPx = Math.Clamp(partPx, 24, 400);
            var key = $"{kind}_{partPx}";
            if (_sprites.TryGetValue(key, out var cached)) return cached;

            int canvas = (int)Math.Ceiling(partPx * 1.45) + 12;
            var bgr = new Mat(canvas, canvas, MatType.CV_8UC3, new Scalar(0, 0, 0));
            var mask = new Mat(canvas, canvas, MatType.CV_8UC1, new Scalar(0));

            int left = (canvas - partPx) / 2, top = (canvas - partPx) / 2;
            var body = new Rect(left, top, partPx, partPx);
            var bodyColor = new Scalar(188, 190, 193);

            Cv2.Rectangle(bgr, body, bodyColor, -1);
            Cv2.Rectangle(mask, body, Scalar.All(255), -1);
            // 边缘高光，模拟金属件倒角反光，同时提升模板的唯一性
            Cv2.Rectangle(bgr, body, new Scalar(232, 234, 236), 1);

            int r = Math.Max(3, partPx / 8);            // 孔半径
            int inset = Math.Max(r + 4, partPx / 5);    // 孔中心到边缘距离
            var holes = new[]
            {
                new Point(left + inset, top + inset),
                new Point(left + partPx - inset, top + inset),
                new Point(left + inset, top + partPx - inset),
                new Point(left + partPx - inset, top + partPx - inset),
            };

            int holeCount = kind == DefectKind.MissingHole ? 3 : 4;
            for (int i = 0; i < holeCount; i++)
            {
                Cv2.Circle(bgr, holes[i], r, new Scalar(40, 42, 45), -1, LineTypes.AntiAlias);
                Cv2.Circle(mask, holes[i], r, Scalar.All(0), -1, LineTypes.AntiAlias);
            }

            // 中心定位槽（本产品的特征位）
            int slotW = Math.Max(6, partPx / 4), slotH = Math.Max(3, partPx / 11);
            var slot = new Rect(canvas / 2 - slotW / 2, canvas / 2 - slotH / 2, slotW, slotH);
            Cv2.Rectangle(bgr, slot, new Scalar(52, 54, 58), -1);
            Cv2.Rectangle(mask, slot, Scalar.All(0), -1);

            var rnd = new Random(seed);
            switch (kind)
            {
                case DefectKind.ExtraMaterial:
                {
                    // 边缘毛刺/多料：向右凸出一块
                    int bw = Math.Max(6, partPx / 7), bh = Math.Max(10, partPx / 3);
                    int bx = body.Right - 2;
                    int by = body.Top + rnd.Next(partPx / 4, partPx / 2);
                    var bump = new Rect(bx, by, bw, bh);
                    Cv2.Rectangle(bgr, bump, bodyColor, -1);
                    Cv2.Rectangle(mask, bump, Scalar.All(255), -1);
                    break;
                }
                case DefectKind.Chip:
                {
                    // 崩边/缺料：啃掉一个角
                    int cw = Math.Max(8, partPx / 4);
                    var bite = new Rect(body.Right - cw, body.Top, cw, cw);
                    Cv2.Rectangle(bgr, bite, new Scalar(0, 0, 0), -1);
                    Cv2.Rectangle(mask, bite, Scalar.All(0), -1);
                    break;
                }
                case DefectKind.Scratch:
                {
                    // 表面划伤：细线，几乎不改变面积，但显著抬高"表面缺陷率"
                    int y0 = body.Top + partPx / 3 + rnd.Next(Math.Max(1, partPx / 12));
                    var p1 = new Point(body.Left + body.Width / 8, y0);
                    var p2 = new Point(body.Right - body.Width / 8, y0 + rnd.Next(-4, 5));
                    Cv2.Line(bgr, p1, p2, new Scalar(72, 74, 78), Math.Max(2, partPx / 48), LineTypes.AntiAlias);
                    break;
                }
            }

            var sprite = new Sprite(bgr, mask, canvas);
            _sprites[key] = sprite;
            return sprite;
        }

        private void ApplyLighting(Mat img, double exposureUs, double gainDb)
        {
            double e = Math.Clamp(exposureUs / 8000.0, 0.25, 3.0);
            double alpha = 0.72 + 0.28 * e;     // 曝光影响对比度
            double beta = (e - 1.0) * 52.0;     // 曝光影响亮度
            img.ConvertTo(img, MatType.CV_8UC3, alpha, beta);

            // 增益越高噪声越大（信噪比下降）
            double noiseWeight = 0.35 + Math.Clamp(gainDb, 0, 24) / 24.0 * 0.55;
            var noise = _noise[_noiseIdx++ % _noise.Length];
            Cv2.AddWeighted(img, 1.0, noise, noiseWeight, -128.0 * noiseWeight, img);
        }

        public void Dispose()
        {
            _background.Dispose();
            foreach (var n in _noise) n.Dispose();
            foreach (var s in _sprites.Values) { s.Bgr.Dispose(); s.Mask.Dispose(); }
            _sprites.Clear();
        }
    }
}
