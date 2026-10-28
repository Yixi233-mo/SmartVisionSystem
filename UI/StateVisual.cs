using SmartVisionSystem.Core;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.UI
{
    /// <summary>状态 → 文案/配色（全系统统一，避免同一个"运行中"在不同页面显示成不同颜色）</summary>
    public static class StateVisual
    {
        public static (string Text, Color Color) Of(StationState s) => s switch
        {
            StationState.Idle => ("空闲", Theme.Muted),
            StationState.Homing => ("回零中", Theme.Warn),
            StationState.Ready => ("就绪", Theme.Accent),
            StationState.Running => ("运行中", Theme.Ok),
            StationState.Stopped => ("已停止", Theme.Warn),
            StationState.Alarm => ("报警", Theme.Ng),
            _ => ("未知", Theme.Muted)
        };

        public static string PhaseText(StationPhase p) => p switch
        {
            StationPhase.None => "—",
            StationPhase.等待来料 => "等待来料",
            StationPhase.相机拍照 => "相机拍照",
            StationPhase.视觉识别 => "视觉识别",
            StationPhase.计算纠偏 => "计算纠偏",
            StationPhase.取放料 => "取放料",
            StationPhase.数据入库 => "数据入库",
            _ => p.ToString()
        };
    }
}
