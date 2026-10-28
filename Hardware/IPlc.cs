namespace SmartVisionSystem.Hardware
{
    /// <summary>
    /// PLC 抽象：视觉系统与产线的握手全部走 IO 点，与真实产线一致（NPN 触发信号 + 结果回传）。
    /// </summary>
    public interface IPlc : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }
        void Open();
        void Close();

        bool Read(string tag);
        void Write(string tag, bool value);
        /// <summary>输出一个脉冲（模拟触发信号）</summary>
        void Pulse(string tag, int widthMs = 50);

        event Action<string, bool>? IoChanged;
    }

    /// <summary>标准 IO 点位表（与电气图纸一致，改现场只需改这里）</summary>
    public static class PlcTags
    {
        public const string SystemRun = "M0 系统运行";
        public const string PartArrived = "X0.0 工件到位";
        public const string CameraReady = "X0.1 相机就绪";
        public const string RobotBusy = "X0.2 机械手忙";
        public const string VisionDone = "Y0.0 检测完成";
        public const string ResultOk = "Y0.1 判定 OK";
        public const string ResultNg = "Y0.2 判定 NG";
        public const string TriggerOut = "Y0.3 相机软触发";
        public const string LineRun = "Y0.4 输送运行";

        public static readonly string[] All =
        {
            SystemRun, PartArrived, CameraReady, RobotBusy, VisionDone, ResultOk, ResultNg, TriggerOut, LineRun
        };
    }
}
