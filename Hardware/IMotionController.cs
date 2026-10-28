namespace SmartVisionSystem.Hardware
{
    /// <summary>轴状态快照（UI 高频读取，值类型避免锁竞争）</summary>
    public readonly record struct MotionSnapshot(double X, double Y, double Z, bool Moving, bool Homed);

    /// <summary>
    /// 运动控制卡抽象（对应固高/雷赛/正运动等卡的三轴点位运动能力）。
    /// 全部方法异步返回，避免"等待轴到位"阻塞 UI 线程 —— 这是工业上位机最常见的卡死原因。
    /// </summary>
    public interface IMotionController : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }
        bool IsHomed { get; }
        bool IsMoving { get; }

        double PosX { get; }
        double PosY { get; }
        double PosZ { get; }

        /// <summary>安全高度：低于该高度禁止 XY 平移（防撞机互锁）</summary>
        double SafeZ { get; set; }

        event Action<MotionSnapshot>? Updated;
        /// <summary>轴告警（超软限位、断线等）</summary>
        event Action<string>? Alarm;

        void Open();
        void Close();

        Task MoveAbsoluteAsync(string axis, double positionMm, CancellationToken ct = default);
        /// <summary>多轴联动到位（null 表示该轴不参与本次移动）</summary>
        Task MoveToAsync(double? x, double? y, double? z, CancellationToken ct = default);
        Task HomeAllAsync(CancellationToken ct = default);
        /// <summary>相对点动：direction = +1 / -1</summary>
        Task JogAsync(string axis, int direction, double distanceMm, CancellationToken ct = default);
        void StopAll();

        void SetVelocity(double maxVelMmS, double accelMmS2);
        (double Min, double Max) GetLimits(string axis);
    }
}
