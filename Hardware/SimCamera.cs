using OpenCvSharp;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.Simulation;

namespace SmartVisionSystem.Hardware
{
    /// <summary>
    /// 虚拟工业相机：不做视频文件播放，而是把虚拟产线实时渲染成图像。
    /// 相比"读 mp4"的假相机，它可以精确控制曝光/增益、模拟取图延迟与丢帧，
    /// 并且画面内容与工件物理位置严格对应，视觉测出的偏差是"真值"，可被用来验证纠偏精度。
    /// </summary>
    public sealed class SimCamera : ICamera
    {
        private readonly VirtualScene _scene;
        private readonly VirtualProductionLine _line;
        private readonly NetworkLink _net;
        private readonly SimulatorSettings _cfg;
        private readonly object _grabLock = new();

        private Mat? _lastFrame;
        private bool _opened;
        private long _grabCount;
        private long _dropCount;

        public SimCamera(VirtualScene scene, VirtualProductionLine line, NetworkLink net, SimulatorSettings cfg)
        {
            _scene = scene;
            _line = line;
            _net = net;
            _cfg = cfg;
            _scene.PartSizeMm = cfg.PartSizeMm;
        }

        public string Name => "SIM-CAM 1280×960 (虚拟 GigE)";
        public bool IsConnected => _opened && _net.IsUp;
        public int Width => _cfg.FrameWidth;
        public int Height => _cfg.FrameHeight;

        public double ExposureUs { get; set; } = 8000;
        public double GainDb { get; set; }

        /// <summary>像素当量（px/mm），与当前配方保持一致</summary>
        public double PixelsPerMm { get; set; } = 2.0;

        public long GrabCount => Interlocked.Read(ref _grabCount);
        public long DropCount => Interlocked.Read(ref _dropCount);

        public void Open()
        {
            _net.ThrowIfDown(Name);
            _opened = true;
            Log.Info("相机", $"{Name} 已连接（曝光 {ExposureUs:F0}us，增益 {GainDb:F0}dB）");
        }

        public void Close()
        {
            _opened = false;
            Log.Info("相机", $"{Name} 已断开");
        }

        public Mat GrabFrame(double timeoutMs = 1500)
        {
            lock (_grabLock)
            {
                if (!_opened) throw new DeviceCommException(Name, "相机未打开");
                _net.ThrowIfDown(Name);

                // 模拟取图延迟（工业相机 + GigE 传输典型 5~15ms）
                int latency = (int)Math.Clamp(_cfg.GrabLatencyMs, 0, 200);
                if (latency > 0) Thread.Sleep(latency);
                _net.ThrowIfDown(Name);

                Interlocked.Increment(ref _grabCount);

                // 模拟丢帧：返回上一帧（真实相机丢帧时并不会报错，而是表现为"图像不更新"）
                if (_cfg.DropFrameRatePct > 0 && Random.Shared.NextDouble() * 100 < _cfg.DropFrameRatePct && _lastFrame != null)
                {
                    Interlocked.Increment(ref _dropCount);
                    return _lastFrame.Clone();
                }

                _scene.PartSizeMm = _cfg.PartSizeMm;
                var frame = _scene.Render(_line.Snapshot(), PixelsPerMm, _line.StationMm, _line.BeltPhaseMm, ExposureUs, GainDb);

                _lastFrame?.Dispose();
                _lastFrame = frame.Clone();
                return frame;
            }
        }

        public void Dispose()
        {
            _lastFrame?.Dispose();
            _lastFrame = null;
        }
    }
}
