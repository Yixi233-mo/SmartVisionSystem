using System.Diagnostics;
using OpenCvSharp;
using SmartVisionSystem.Infrastructure;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace SmartVisionSystem.Core
{
    /// <summary>
    /// 视觉引擎：灰度化 → 多角度模板匹配（含亚像素）→ 材料面积测量（扣内孔）→ 表面缺陷分析 → 综合判定。
    /// 
    /// 设计要点（对应非功能需求）：
    /// 1. 无状态静态实现，天然线程安全，可被工位线程直接调用；
    /// 2. 先 0° 快匹配后逐级扩大角度搜索（命中即退出），典型 CT 12~20ms，最坏 < 50ms；
    /// 3. 搜索窗口限定在基准点附近，既提速又能避免"抓到旁边的工件"误判。
    /// </summary>
    public static class VisionEngine
    {
        /// <summary>判定为"真实孔洞"的最小轮廓面积（px²）。小于该值视为划伤/纹理，不作为材料缺失处理。</summary>
        private const double HoleMinAreaPx = 80;

        /// <summary>执行一次完整检测</summary>
        public static VisionResult Inspect(Mat frameBgr, Recipe recipe)
        {
            var sw = Stopwatch.StartNew();
            double baseX = recipe.ImageBaseX, baseY = recipe.ImageBaseY;

            if (frameBgr == null || frameBgr.Empty())
                return VisionResult.Failed("图像为空", sw.Elapsed.TotalMilliseconds, baseX, baseY);
            if (recipe.TemplateGray == null || recipe.TemplateGray.Empty())
                return VisionResult.Failed("未建立视觉模板", sw.Elapsed.TotalMilliseconds, baseX, baseY);

            using var gray = ToGray(frameBgr);

            // ── 1. 模板匹配定位 ──
            if (!Locate(gray, recipe, out var loc))
                return VisionResult.Failed($"定位失败（模板相似度不足）", sw.Elapsed.TotalMilliseconds, baseX, baseY);

            var result = new VisionResult
            {
                Score = loc.Score,
                AngleDeg = loc.Angle,
                MatchedRect = loc.Rect,
                CenterX = (int)Math.Round(loc.CenterX),
                CenterY = (int)Math.Round(loc.CenterY),
                BaseImageX = baseX,
                BaseImageY = baseY
            };

            // ── 2. 偏差计算（像素 → 毫米，交运动控制卡纠偏）──
            result.OffsetXPx = loc.CenterX - baseX;
            result.OffsetYPx = loc.CenterY - baseY;
            double pxPerMm = Math.Max(0.1, recipe.PixelsPerMm);
            result.OffsetXmm = result.OffsetXPx / pxPerMm;
            result.OffsetYmm = result.OffsetYPx / pxPerMm;

            // ── 3. 测量：材料面积 + 表面缺陷 ──
            var measure = MeasureRegion(gray, Expand(loc.Rect, 14, gray.Width, gray.Height));
            result.MaterialAreaPx = measure.Area;
            result.SurfaceRatio = measure.SurfaceRatio;
            double nominal = (recipe.MinAreaPx + recipe.MaxAreaPx) / 2.0;
            result.AreaDeviationPct = nominal > 1 ? (measure.Area - nominal) / nominal * 100.0 : 0;

            // ── 4. 综合判定 ──
            var reasons = new List<string>();
            if (loc.Score < recipe.MatchThreshold)
                reasons.Add($"匹配低({loc.Score:F2}<{recipe.MatchThreshold:F2})");
            if (measure.Area < recipe.MinAreaPx)
                reasons.Add($"缺料/面积小({measure.Area:F0}<{recipe.MinAreaPx:F0})");
            else if (measure.Area > recipe.MaxAreaPx)
                reasons.Add($"多料/面积大({measure.Area:F0}>{recipe.MaxAreaPx:F0})");
            if (measure.SurfaceRatio > recipe.MaxSurfaceDefectRatio)
                reasons.Add($"表面缺陷({measure.SurfaceRatio * 100:F2}%>{recipe.MaxSurfaceDefectRatio * 100:F2}%)");

            result.IsOk = reasons.Count == 0;
            result.NgReason = string.Join("；", reasons);
            result.CtMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        // ─────────────────────────── 定位 ───────────────────────────

        public readonly record struct LocateResult(double Score, double Angle, Rect Rect, double CenterX, double CenterY);

        /// <summary>多角度模板匹配。0° 先试，命中最优即提前结束。</summary>
        public static bool Locate(Mat gray, Recipe recipe, out LocateResult best)
        {
            best = default;
            var tpl = recipe.TemplateGray;
            if (tpl == null || tpl.Empty()) return false;

            // 搜索窗口：以视觉基准点为中心
            int pad = Math.Max(20, recipe.SearchWindowPx);
            int w = tpl.Width + pad * 2;
            int h = tpl.Height + pad * 2;
            int x = (int)Math.Round(recipe.ImageBaseX - tpl.Width / 2.0) - pad;
            int y = (int)Math.Round(recipe.ImageBaseY - tpl.Height / 2.0) - pad;
            var roiRect = Clamp(new Rect(x, y, w, h), gray.Width, gray.Height);
            if (roiRect.Width < tpl.Width || roiRect.Height < tpl.Height) return false;

            using var roi = new Mat(gray, roiRect);

            double range = Math.Clamp(recipe.SearchAngleDeg, 0, 30);
            double step = Math.Clamp(recipe.SearchAngleStep, 1, 15);
            var angles = BuildAngles(range, step);

            double bestScore = -1;
            double bestAngle = 0;
            Point bestPt = default;
            Mat? bestMap = null;

            foreach (var angle in angles)
            {
                Mat probe = tpl;
                Mat? rotated = null;
                try
                {
                    if (Math.Abs(angle) > 0.01)
                    {
                        rotated = RotateTemplate(tpl, angle);
                        probe = rotated;
                    }
                    if (probe.Width > roi.Width || probe.Height > roi.Height) continue;

                    using var map = new Mat();
                    Cv2.MatchTemplate(roi, probe, map, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(map, out _, out double maxVal, out _, out Point maxLoc);
                    if (maxVal > bestScore)
                    {
                        bestScore = maxVal;
                        bestAngle = angle;
                        bestPt = maxLoc;
                        bestMap?.Dispose();
                        bestMap = map.Clone();
                    }
                    // 已经非常确信，无需继续扩大角度搜索（保证 CT 稳定）
                    if (maxVal >= 0.97) break;
                }
                catch (Exception ex)
                {
                    Log.Error("视觉", $"角度 {angle}° 匹配异常", ex);
                }
                finally
                {
                    rotated?.Dispose();
                }
            }

            if (bestMap == null || bestScore < 0) return false;

            // 亚像素插值：抛物线拟合，把定位精度从 ±0.5px 提升到 ±0.05px
            var (subX, subY) = SubPixel(bestMap, bestPt);
            bestMap.Dispose();

            double cx = roiRect.X + subX + tpl.Width / 2.0;
            double cy = roiRect.Y + subY + tpl.Height / 2.0;

            best = new LocateResult(bestScore, bestAngle,
                new Rect((int)Math.Round(roiRect.X + subX), (int)Math.Round(roiRect.Y + subY), tpl.Width, tpl.Height),
                cx, cy);
            return true;
        }

        private static List<double> BuildAngles(double range, double step)
        {
            var list = new List<double> { 0 };
            if (range < step) return list;
            for (double a = step; a <= range + 1e-6; a += step)
            {
                list.Add(-a);
                list.Add(a);
            }
            return list;
        }

        private static Mat RotateTemplate(Mat tpl, double angleDeg)
        {
            var center = new Point2f(tpl.Width / 2f, tpl.Height / 2f);
            using var m = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
            var dst = new Mat();
            Cv2.WarpAffine(tpl, dst, m, tpl.Size(),
                InterpolationFlags.Linear, BorderTypes.Replicate);
            return dst;
        }

        /// <summary>对匹配得分图的极值点做二次曲面拟合，求亚像素坐标</summary>
        private static (double x, double y) SubPixel(Mat map, Point p)
        {
            if (map.Type() != MatType.CV_32F) return (p.X, p.Y);
            if (p.X <= 0 || p.Y <= 0 || p.X >= map.Cols - 1 || p.Y >= map.Rows - 1) return (p.X, p.Y);

            float c = map.At<float>(p.Y, p.X);
            float l = map.At<float>(p.Y, p.X - 1), r = map.At<float>(p.Y, p.X + 1);
            float t = map.At<float>(p.Y - 1, p.X), b = map.At<float>(p.Y + 1, p.X);

            double dx = Offset(l, c, r);
            double dy = Offset(t, c, b);
            return (p.X + Math.Clamp(dx, -0.5, 0.5), p.Y + Math.Clamp(dy, -0.5, 0.5));

            static double Offset(double left, double center, double right)
            {
                double denom = left - 2 * center + right;
                if (Math.Abs(denom) < 1e-9) return 0;
                return (left - right) / (2 * denom);
            }
        }

        // ─────────────────────────── 测量 ───────────────────────────

        public readonly record struct MeasureResult(double Area, double SurfaceRatio, int HoleCount, double CenterX, double CenterY);

        /// <summary>
        /// 测量材料面积：外轮廓面积 - 内孔面积。缺孔→面积变大（判定多料），崩边/缺料→面积变小。
        /// </summary>
        public static MeasureResult MeasureRegion(Mat gray, Rect roiRect)
        {
            roiRect = Clamp(roiRect, gray.Width, gray.Height);
            if (roiRect.Width < 10 || roiRect.Height < 10) return new MeasureResult(0, 0, 0, 0, 0);

            using var roi = new Mat(gray, roiRect);
            using var bin = new Mat();
            Cv2.Threshold(roi, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            // 注意：这里绝不能做开运算——3×3 开运算会把宽度 1~2px 的划伤整条擦除，
            // 导致"表面划伤"这类缺陷永远检不出。噪点由 Otsu + 面积阈值过滤即可。

            Cv2.FindContours(bin, out Point[][] contours, out HierarchyIndex[] hierarchy,
                RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) return new MeasureResult(0, 0, 0, 0, 0);

            // 最大的外轮廓（parent == -1）
            int outerIdx = -1;
            double outerArea = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                if (hierarchy[i].Parent != -1) continue;
                double a = Cv2.ContourArea(contours[i]);
                if (a > outerArea) { outerArea = a; outerIdx = i; }
            }
            if (outerIdx < 0 || outerArea < 50) return new MeasureResult(0, 0, 0, 0, 0);

            // 扣除内孔
            double holeArea = 0;
            int holeCount = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                if (hierarchy[i].Parent != outerIdx) continue;
                double a = Cv2.ContourArea(contours[i]);
                if (a >= HoleMinAreaPx) { holeArea += a; holeCount++; }
            }

            var moments = Cv2.Moments(contours[outerIdx]);
            double cx = moments.M00 > 1e-6 ? moments.M10 / moments.M00 : roiRect.Width / 2.0;
            double cy = moments.M00 > 1e-6 ? moments.M01 / moments.M00 : roiRect.Height / 2.0;

            // 表面缺陷：轮廓内部（腐蚀 3px 排除边缘、并剔除内孔）显著暗于平均值的像素占比
            double surfaceRatio = 0;
            using (var filled = new Mat(roi.Size(), MatType.CV_8UC1, Scalar.All(0)))
            using (var inner = new Mat())
            {
                Cv2.DrawContours(filled, contours, outerIdx, Scalar.All(255), -1);
                // 内孔不属于"材料表面"，必须先挖掉，否则孔洞会被误算成表面缺陷
                for (int i = 0; i < contours.Length; i++)
                {
                    if (hierarchy[i].Parent != outerIdx) continue;
                    if (Cv2.ContourArea(contours[i]) >= HoleMinAreaPx)
                        Cv2.DrawContours(filled, contours, i, Scalar.All(0), -1);
                }
                Cv2.Erode(filled, inner, kernel, iterations: 3);
                int innerCount = Cv2.CountNonZero(inner);
                if (innerCount > 200)
                {
                    double mean = Cv2.Mean(roi, inner).Val0;
                    double darkLimit = Math.Max(10, mean - 45);
                    using var dark = new Mat();
                    Cv2.InRange(roi, new Scalar(0), new Scalar(darkLimit), dark);
                    Cv2.BitwiseAnd(dark, inner, dark);
                    surfaceRatio = Cv2.CountNonZero(dark) / (double)innerCount;
                }
            }

            return new MeasureResult(outerArea - holeArea, surfaceRatio, holeCount,
                roiRect.X + cx, roiRect.Y + cy);
        }

        /// <summary>辅助功能：在当前图像中自动寻找最亮的工件轮廓（用于一键建立模板/标定）</summary>
        public static bool AutoDetectPart(Mat frameBgr, out Rect rect, out double area)
        {
            rect = default; area = 0;
            using var gray = ToGray(frameBgr);
            using var bin = new Mat();
            Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
            Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kernel, iterations: 2);
            Cv2.FindContours(bin, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) return false;

            double best = 0;
            Rect bestRect = default;
            foreach (var c in contours)
            {
                double a = Cv2.ContourArea(c);
                var b = Cv2.BoundingRect(c);
                if (a > best && b.Width > 20 && b.Height > 20 && b.Width < frameBgr.Width * 0.9 && b.Height < frameBgr.Height * 0.9)
                {
                    best = a; bestRect = b;
                }
            }
            if (best <= 0) return false;
            rect = bestRect;
            area = best;
            return true;
        }

        // ─────────────────────────── 工具 ───────────────────────────

        private static Mat ToGray(Mat src)
        {
            if (src.Channels() == 1) return src.Clone();
            var gray = new Mat();
            Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            return gray;
        }

        private static Rect Clamp(Rect r, int w, int h)
        {
            int x = Math.Clamp(r.X, 0, Math.Max(0, w - 1));
            int y = Math.Clamp(r.Y, 0, Math.Max(0, h - 1));
            int rw = Math.Clamp(r.Width, 1, w - x);
            int rh = Math.Clamp(r.Height, 1, h - y);
            return new Rect(x, y, rw, rh);
        }

        private static Rect Expand(Rect r, int pad, int w, int h)
            => Clamp(new Rect(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2), w, h);
    }
}
