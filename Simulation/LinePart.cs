namespace SmartVisionSystem.Simulation
{
    /// <summary>注入的不良类型</summary>
    public enum DefectKind
    {
        None = 0,
        MissingHole = 1,     // 漏打孔 → 材料面积变大
        ExtraMaterial = 2,   // 毛刺/多料 → 材料面积变大
        Chip = 3,            // 崩边/缺料 → 材料面积变小
        Scratch = 4          // 表面划伤 → 面积不变，表面缺陷率升高
    }

    public enum PartStage
    {
        Queueing,   // 排队中
        AtStation,  // 停在工位
        Inspecting, // 检测中
        Done,       // 已完成，随线体流出
        Exited      // 已离开视野
    }

    /// <summary>
    /// 线体上的一个工件实例：物理位置 + 来料偏差 + 注入缺陷。
    /// </summary>
    public sealed class LinePart
    {
        public required string SN { get; init; }
        public required double PositionMm { get; set; }
        /// <summary>横向（沿输送方向）来料偏差 px —— 即视觉测得 OffsetX 的来源</summary>
        public double OffsetXPx { get; init; }
        /// <summary>纵向（垂直于输送方向）来料偏差 px —— 即视觉测得 OffsetY 的来源</summary>
        public double OffsetYPx { get; init; }
        /// <summary>来料旋转角 deg</summary>
        public double AngleDeg { get; init; }
        public DefectKind Defect { get; init; }
        /// <summary>用于绘制缺陷时随机化位置</summary>
        public int Seed { get; init; }

        public PartStage Stage { get; set; } = PartStage.Queueing;
        public DateTime SpawnTime { get; init; } = DateTime.Now;
        public DateTime? InspectTime { get; set; }
        /// <summary>检测结论（由工位回填，用于产线可视化着色）</summary>
        public bool? InspectedOk { get; set; }

        public bool IsNgExpected => Defect != DefectKind.None;

        public override string ToString() => $"{SN}@{PositionMm:F0}mm {Stage} {(Defect == DefectKind.None ? "良品" : Defect.ToString())}";
    }
}
