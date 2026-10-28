using SmartVisionSystem.Hardware;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.Simulation;

namespace SmartVisionSystem.Core
{
    /// <summary>
    /// 设备总线（单例）：全系统只有"一台机器"的状态，所有界面读同一份设备实例。
    /// 这修掉了初版 MVP 里每个 View 各自 new 一个 MockMotion 导致"运动页面动了、监控页面不知道"的架构缺陷。
    /// </summary>
    public sealed class DeviceHub : IDisposable
    {
        private static readonly Lazy<DeviceHub> _lazy = new(() => new DeviceHub(), LazyThreadSafetyMode.ExecutionAndPublication);
        public static DeviceHub I => _lazy.Value;

        private bool _initialized;

        public SimulatorSettings Settings { get; private set; } = new();
        public VirtualScene Scene { get; private set; } = null!;
        public VirtualProductionLine Line { get; private set; } = null!;
        public NetworkLink Network { get; private set; } = null!;
        public SimCamera Camera { get; private set; } = null!;
        public SimMotionController Motion { get; private set; } = null!;
        public SimPlc Plc { get; private set; } = null!;
        public DatabaseManager Db { get; private set; } = null!;
        public StationController Station { get; private set; } = null!;

        public bool IsInitialized => _initialized;
        public Recipe CurrentRecipe => RecipeStore.Current;

        /// <summary>初始化全部虚拟硬件与业务对象（幂等）</summary>
        public void Initialize()
        {
            if (_initialized) return;

            Settings = SimulatorSettings.Load();
            Log.Info("系统", $"加载仿真参数：线速 {Settings.LineSpeedMmS:F0}mm/s，间距 {Settings.PartPitchMm:F0}mm，缺陷率 {Settings.DefectRatePct:F0}%");

            Scene = new VirtualScene(Settings.FrameWidth, Settings.FrameHeight) { PartSizeMm = Settings.PartSizeMm };
            Line = new VirtualProductionLine(Settings);
            Network = new NetworkLink();
            Camera = new SimCamera(Scene, Line, Network, Settings);
            Motion = new SimMotionController(Network, Settings);
            Plc = new SimPlc(Network);
            Db = new DatabaseManager();

            // 恢复上次配方；首次运行则自动生成演示配方（含模板与阈值）
            RecipeStore.RestoreLastOrDefault();
            if (!RecipeStore.Current.HasTemplate)
            {
                Log.Warn("系统", "未找到可用配方，正在根据虚拟标样自动生成演示配方…");
                DemoRecipeFactory.EnsureDemo(Scene, 2.0);
            }

            var r = RecipeStore.Current;
            Camera.PixelsPerMm = r.PixelsPerMm;
            Camera.ExposureUs = r.ExposureUs;
            Camera.GainDb = r.GainDb;

            Camera.Open();
            Motion.Open();
            Plc.Open();

            Station = new StationController(Line, Camera, Motion, Plc, Db, Network);
            Station.Initialize();

            _initialized = true;
            Log.Info("系统", $"设备总线初始化完成：{Camera.Name} / {Motion.Name} / {Plc.Name}，当前配方 {r.Name}");
        }

        /// <summary>仿真参数变更后同步到硬件对象</summary>
        public void ApplySettings()
        {
            Motion.SetVelocity(Settings.MaxVelMmS, Settings.AccelMmS2);
            Scene.PartSizeMm = Settings.PartSizeMm;
            Settings.Save();
        }

        /// <summary>模拟拔网线（演示断线重连）</summary>
        public void SimulateNetworkDrop(int durationMs = 3000) => Network.DropFor(durationMs);

        public void Dispose()
        {
            try { Station?.Dispose(); } catch { }
            try { Line?.Dispose(); } catch { }
            try { Camera?.Dispose(); } catch { }
            try { Motion?.Dispose(); } catch { }
            try { Plc?.Dispose(); } catch { }
            try { Scene?.Dispose(); } catch { }
            try { Db?.Dispose(); } catch { }
            _initialized = false;
        }
    }
}
