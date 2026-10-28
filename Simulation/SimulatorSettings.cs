using System.Text.Json;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.Simulation
{
    /// <summary>
    /// 仿真参数：把"真实产线"的可调物理量集中到一处，UI 可实时调整并落盘。
    /// 这些参数决定了虚拟来料的偏差分布、缺陷比例与输送节拍，是纯软件环境下验证算法鲁棒性的核心旋钮。
    /// </summary>
    public sealed class SimulatorSettings
    {
        // ── 产线 ──
        /// <summary>皮带线速度 mm/s</summary>
        public double LineSpeedMmS { get; set; } = 220;
        /// <summary>来料间距 mm</summary>
        public double PartPitchMm { get; set; } = 240;
        /// <summary>线体上同时在线的工件数</summary>
        public int QueueLength { get; set; } = 3;
        /// <summary>线体总长 mm（用于可视化）</summary>
        public double LineLengthMm { get; set; } = 3600;

        // ── 来料离散度（视觉定位存在的前提）──
        /// <summary>来料位置偏差幅度 ±px</summary>
        public double OffsetAmpPx { get; set; } = 22;
        /// <summary>来料旋转角度偏差 ±deg</summary>
        public double AngleAmpDeg { get; set; } = 6;

        // ── 缺陷注入 ──
        /// <summary>不良品比例 %</summary>
        public double DefectRatePct { get; set; } = 15;
        public bool EnableScratch { get; set; } = true;
        public bool EnableMissingHole { get; set; } = true;
        public bool EnableChip { get; set; } = true;
        public bool EnableExtraMaterial { get; set; } = true;
        /// <summary>是否注入"超视野/大偏移"极端件（测试防呆）</summary>
        public bool EnableOutlier { get; set; }
        /// <summary>暂停缺陷注入（调试期只出良品，便于标定）</summary>
        public bool PauseDefectInjection { get; set; }

        // ── 工件 ──
        /// <summary>工件外形尺寸 mm</summary>
        public double PartSizeMm { get; set; } = 40;

        // ── 相机 ──
        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 960;
        /// <summary>模拟取图延迟 ms（工业相机典型 5~15ms）</summary>
        public double GrabLatencyMs { get; set; } = 7;
        /// <summary>模拟丢帧率 %（0 表示稳定）</summary>
        public double DropFrameRatePct { get; set; }

        // ── 运动 ──
        /// <summary>轴最大速度 mm/s</summary>
        public double MaxVelMmS { get; set; } = 400;
        /// <summary>轴加速度 mm/s²</summary>
        public double AccelMmS2 { get; set; } = 2400;
        /// <summary>点动速度 mm/s</summary>
        public double JogVelMmS { get; set; } = 40;

        // ── 落盘 ──
        private static readonly string FilePath = Path.Combine(AppPaths.DataDir, "simulator.json");
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static SimulatorSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<SimulatorSettings>(File.ReadAllText(FilePath), JsonOpts);
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                Log.Error("仿真", "载入仿真参数失败，使用默认值", ex);
            }
            return new SimulatorSettings();
        }

        public void Save()
        {
            try { System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
            catch (Exception ex) { Log.Error("仿真", "保存仿真参数失败", ex); }
        }

        public SimulatorSettings Clone() => (SimulatorSettings)MemberwiseClone();
    }
}
