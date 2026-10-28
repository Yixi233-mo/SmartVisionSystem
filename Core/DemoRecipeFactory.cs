using OpenCvSharp;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.Simulation;

namespace SmartVisionSystem.Core
{
    /// <summary>
    /// 演示配方自动生成器：首次启动时，从"虚拟标称件"渲染图中自动提取模板并学习判定阈值。
    ///
    /// 这解决了"没有图纸文件也能立刻跑起来"的问题，同时演示了正确的工程做法——
    /// 模板必须取自与生产同源的成像结果（而非 CAD 图纸），判定阈值应由标称件实测面积反推。
    /// </summary>
    public static class DemoRecipeFactory
    {
        public const string DemoName = "产品-A";

        public static bool EnsureDemo(VirtualScene scene, double pxPerMm = 2.0)
        {
            try
            {
                var recipe = Build(scene, pxPerMm, DemoName);
                if (recipe == null) return false;
                if (!RecipeStore.Save(recipe)) return false;
                RecipeStore.Apply(recipe);
                Log.Info("配方", $"已自动生成演示配方【{recipe.Name}】：模板 {recipe.Template!.Width}×{recipe.Template.Height}px，" +
                                 $"材料面积 {recipe.MinAreaPx:F0}~{recipe.MaxAreaPx:F0}px²，基准点 ({recipe.ImageBaseX:F0},{recipe.ImageBaseY:F0})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("配方", "生成演示配方失败", ex);
                return false;
            }
        }

        /// <summary>以真实感画面建立配方（供首次运行与"一键重建模板"使用）</summary>
        public static Recipe? Build(VirtualScene scene, double pxPerMm, string name)
        {
            using var frame = scene.RenderNominal(pxPerMm);
            if (frame.Empty()) return null;

            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

            if (!VisionEngine.AutoDetectPart(frame, out var partRect, out _))
            {
                Log.Warn("配方", "自动定位工件失败，无法生成模板");
                return null;
            }

            // 模板只留 3px 余量：余量越大，混入的背景像素越多，
            // 旋转搜索时背景与工件转动不同步会把相关峰拉偏，直接体现为纠偏偏差（实测 8px 余量带来 0.17mm 误差）
            int pad = 3;
            var tplRect = new Rect(partRect.X - pad, partRect.Y - pad, partRect.Width + pad * 2, partRect.Height + pad * 2);
            tplRect = new Rect(
                Math.Clamp(tplRect.X, 0, gray.Width - 1),
                Math.Clamp(tplRect.Y, 0, gray.Height - 1),
                Math.Clamp(tplRect.Width, 1, gray.Width - Math.Clamp(tplRect.X, 0, gray.Width - 1)),
                Math.Clamp(tplRect.Height, 1, gray.Height - Math.Clamp(tplRect.Y, 0, gray.Height - 1)));

            using var tplView = new Mat(gray, tplRect);
            var template = tplView.Clone();

            // 学习材料面积（与运行期使用同一套测量算法，保证口径一致）
            var measureRect = new Rect(partRect.X - 14, partRect.Y - 14, partRect.Width + 28, partRect.Height + 28);
            measureRect = new Rect(
                Math.Clamp(measureRect.X, 0, gray.Width - 1),
                Math.Clamp(measureRect.Y, 0, gray.Height - 1),
                Math.Clamp(measureRect.Width, 1, gray.Width - Math.Clamp(measureRect.X, 0, gray.Width - 1)),
                Math.Clamp(measureRect.Height, 1, gray.Height - Math.Clamp(measureRect.Y, 0, gray.Height - 1)));
            var measure = VisionEngine.MeasureRegion(gray, measureRect);

            if (measure.Area < 100)
            {
                Log.Warn("配方", "标称件面积测量异常，放弃自动生成");
                template.Dispose();
                return null;
            }

            // 容差 ±3.5%：足以覆盖成像噪声与来料旋转带来的测量波动，同时能抓住 ≥5% 的真实缺陷
            double area = measure.Area;
            var recipe = new Recipe
            {
                Name = name,
                Description = "由系统根据虚拟标称件自动生成",
                PixelsPerMm = pxPerMm,
                Template = template,
                TemplateGray = template.Clone(),
                ImageBaseX = partRect.X + partRect.Width / 2.0,
                ImageBaseY = partRect.Y + partRect.Height / 2.0,
                MinAreaPx = Math.Round(area * 0.965),
                MaxAreaPx = Math.Round(area * 1.035),
                MaxSurfaceDefectRatio = Math.Max(0.004, Math.Round(measure.SurfaceRatio * 3, 4)),
                MatchThreshold = 0.80,
                SearchWindowPx = 90,
                SearchAngleDeg = 8,
                SearchAngleStep = 2,
                RobotBaseX = 250,
                RobotBaseY = 250,
                ZSafe = 80,
                ZPick = 5,
                ZPlace = 10,
                PlaceX = 420,
                PlaceY = 160,
                PlaceNgX = 420,
                PlaceNgY = 380,
                ExposureUs = 8000,
                GainDb = 0,
                MaxCycleMs = 3500
            };

            Log.Info("配方", $"模板学习完成：材料面积 {area:F0}px²（孔数 {measure.HoleCount}），表面缺陷基准 {measure.SurfaceRatio * 100:F2}%");
            return recipe;
        }
    }
}
