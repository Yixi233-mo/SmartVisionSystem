using OpenCvSharp;

namespace SmartVisionSystem.Core
{
    /// <summary>单次视觉检测的结构化结果（含定位、测量、判定全过程数据，供 UI / 数据库 / MES 共用）</summary>
    public sealed class VisionResult
    {
        public bool IsOk { get; set; }
        public string NgReason { get; set; } = "";
        public double Score { get; set; }
        public double AngleDeg { get; set; }

        /// <summary>相对视觉基准的偏差（mm）——送给运动控制卡做纠偏的量</summary>
        public double OffsetXmm { get; set; }
        public double OffsetYmm { get; set; }
        /// <summary>偏差（px），用于追溯与标定分析</summary>
        public double OffsetXPx { get; set; }
        public double OffsetYPx { get; set; }

        /// <summary>材料面积 px²（外轮廓减内孔），用于缺料/多料判定</summary>
        public double MaterialAreaPx { get; set; }
        public double AreaDeviationPct { get; set; }
        /// <summary>表面缺陷像素占比（划伤/污点）</summary>
        public double SurfaceRatio { get; set; }

        /// <summary>本次视觉处理耗时（ms）</summary>
        public double CtMs { get; set; }

        public int CenterX { get; set; }
        public int CenterY { get; set; }
        public Rect MatchedRect { get; set; }
        /// <summary>基准点在图像中的位置</summary>
        public double BaseImageX { get; set; }
        public double BaseImageY { get; set; }

        public static VisionResult Failed(string reason, double ctMs, double baseX, double baseY) => new()
        {
            IsOk = false,
            NgReason = reason,
            CtMs = ctMs,
            BaseImageX = baseX,
            BaseImageY = baseY
        };
    }
}
