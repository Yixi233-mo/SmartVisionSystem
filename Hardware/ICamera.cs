using OpenCvSharp;

namespace SmartVisionSystem.Hardware
{
    /// <summary>设备通讯异常：相机断线 / PLC 掉线 / 网线被拔 统一用它抛出，让上层能明确区分"业务NG"与"设备故障"</summary>
    public sealed class DeviceCommException : Exception
    {
        public string Device { get; }
        public DeviceCommException(string device, string message) : base(message) => Device = device;
    }

    /// <summary>工业相机抽象。换成海康/大恒/Basler 时，只需再实现一个 ICamera 即可，上层业务零改动。</summary>
    public interface ICamera : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }
        int Width { get; }
        int Height { get; }

        double ExposureUs { get; set; }
        double GainDb { get; set; }

        void Open();
        void Close();

        /// <summary>软触发抓取一帧（调用方负责 Dispose 返回的 Mat）</summary>
        Mat GrabFrame(double timeoutMs = 1500);
    }
}
