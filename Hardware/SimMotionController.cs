using System.Diagnostics;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.Simulation;

namespace SmartVisionSystem.Hardware
{
    /// <summary>
    /// 三轴运动控制仿真：按真实伺服的行为建模——梯形速度曲线（加速→匀速→减速）、软限位拦截、
    /// Z 轴安全高度互锁、回零时序、以及断电/断线时的急停。
    ///
    /// 关键点：所有"等轴到位"都是异步等待，绝不在 UI 线程 sleep；
    /// 位置更新由独立的 4ms 物理线程推进，UI 只订阅 25Hz 的快照事件 —— 这是 7×24 稳定运行的基础。
    /// </summary>
    public sealed class SimMotionController : IMotionController
    {
        private sealed class Axis
        {
            public string Name = "";
            public double Min, Max;
            public double Pos, Target;
            public double Vel;
            public double MaxVel = 400;
            public double Acc = 2400;
            public double NominalMaxVel = 400;
        }

        private readonly Axis _x = new() { Name = "X", Min = 0, Max = 800, Pos = 250, Target = 250, MaxVel = 400, NominalMaxVel = 400 };
        private readonly Axis _y = new() { Name = "Y", Min = 0, Max = 600, Pos = 250, Target = 250, MaxVel = 400, NominalMaxVel = 400 };
        private readonly Axis _z = new() { Name = "Z", Min = 0, Max = 150, Pos = 80, Target = 80, MaxVel = 300, NominalMaxVel = 300 };

        private readonly NetworkLink _net;
        private readonly SimulatorSettings _cfg;
        private readonly object _sync = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private Thread? _thread;
        private volatile bool _loop;
        private volatile bool _isHomed;
        private volatile bool _opened;
        private double _lastT;
        private double _lastNotify;
        private bool _wasMoving;

        public SimMotionController(NetworkLink net, SimulatorSettings cfg)
        {
            _net = net;
            _cfg = cfg;
            SetVelocity(cfg.MaxVelMmS, cfg.AccelMmS2);
        }

        public string Name => "SIM-MOTION 3轴 (虚拟运动控制卡)";
        public bool IsConnected => _opened && _net.IsUp;
        public bool IsHomed => _isHomed;
        public bool IsMoving => IsAxisMoving(_x) || IsAxisMoving(_y) || IsAxisMoving(_z);

        public double PosX => _x.Pos;
        public double PosY => _y.Pos;
        public double PosZ => _z.Pos;

        /// <summary>安全高度：低于它禁止任何 XY 平移（防撞机）</summary>
        public double SafeZ { get; set; } = 60;

        public event Action<MotionSnapshot>? Updated;
        public event Action<string>? Alarm;

        // ─────────────────────────── 连接 ───────────────────────────

        public void Open()
        {
            _net.ThrowIfDown(Name);
            _opened = true;
            EnsureLoop();
            Log.Info("运动", $"{Name} 已连接，行程 X[0,800] Y[0,600] Z[0,150]，速度 {(_x.MaxVel):F0}mm/s");
        }

        public void Close()
        {
            StopAll();
            _opened = false;
            Log.Info("运动", $"{Name} 已断开");
        }

        public void SetVelocity(double maxVelMmS, double accelMmS2)
        {
            foreach (var a in new[] { _x, _y, _z })
            {
                a.NominalMaxVel = Math.Clamp(maxVelMmS, 5, 2000);
                a.MaxVel = a.NominalMaxVel;
                a.Acc = Math.Clamp(accelMmS2, 100, 20000);
            }
        }

        public (double Min, double Max) GetLimits(string axis)
        {
            var a = GetAxis(axis);
            return (a.Min, a.Max);
        }

        // ─────────────────────────── 运动指令 ───────────────────────────

        public async Task MoveAbsoluteAsync(string axis, double positionMm, CancellationToken ct = default)
        {
            _net.ThrowIfDown(Name);
            var a = GetAxis(axis);
            SetTarget(a, positionMm);
            _wake.Set();
            await WaitIdleAsync(a, ct).ConfigureAwait(false);
        }

        public async Task MoveToAsync(double? x, double? y, double? z, CancellationToken ct = default)
        {
            _net.ThrowIfDown(Name);

            // 防撞机互锁：Z 还压在低位时不允许 XY 平移
            bool needXy = x.HasValue || y.HasValue;
            if (needXy && _z.Pos < SafeZ - 0.01 && !(z.HasValue && z.Value >= SafeZ))
            {
                Log.Warn("运动", $"互锁触发：Z 轴 {_z.Pos:F1}mm 低于安全高度 {SafeZ:F0}mm，先自动抬升再平移");
                await MoveAbsoluteAsync("Z", SafeZ, ct).ConfigureAwait(false);
            }

            if (x.HasValue) SetTarget(_x, x.Value);
            if (y.HasValue) SetTarget(_y, y.Value);
            if (z.HasValue) SetTarget(_z, z.Value);
            _wake.Set();

            ct.ThrowIfCancellationRequested();
            while (IsMoving)
            {
                await Task.Delay(4, ct).ConfigureAwait(false);
            }
        }

        public async Task JogAsync(string axis, int direction, double distanceMm, CancellationToken ct = default)
        {
            var a = GetAxis(axis);
            double target = a.Pos + Math.Sign(direction) * Math.Abs(distanceMm);
            if (a == _z)
                await MoveAbsoluteAsync("Z", target, ct).ConfigureAwait(false);
            else
                await MoveToAsync(a == _x ? target : null, a == _y ? target : null, null, ct).ConfigureAwait(false);
        }

        public async Task HomeAllAsync(CancellationToken ct = default)
        {
            _net.ThrowIfDown(Name);
            EnsureLoop();
            Log.Info("运动", "开始全轴回零：Z 先抬至安全高度 → X/Y 依次回原点");
            _isHomed = false;

            // 回零速度降为 35%，符合真实伺服回零规范
            foreach (var a in new[] { _x, _y, _z }) a.MaxVel = Math.Max(10, a.NominalMaxVel * 0.35);
            try
            {
                await MoveAbsoluteAsync("Z", SafeZ, ct).ConfigureAwait(false);
                await MoveAbsoluteAsync("X", _x.Min, ct).ConfigureAwait(false);
                await MoveAbsoluteAsync("Y", _y.Min, ct).ConfigureAwait(false);
                lock (_sync)
                {
                    _x.Pos = _x.Target = 0; _y.Pos = _y.Target = 0; _z.Pos = _z.Target = SafeZ;
                    _x.Vel = _y.Vel = _z.Vel = 0;
                }
                _isHomed = true;
                Log.Info("运动", "全轴回零完成，原点已建立");
            }
            finally
            {
                foreach (var a in new[] { _x, _y, _z }) a.MaxVel = a.NominalMaxVel;
                FireUpdated();
            }
        }

        public void StopAll()
        {
            lock (_sync)
            {
                foreach (var a in new[] { _x, _y, _z })
                {
                    a.Target = a.Pos;
                    a.Vel = 0;
                }
            }
            _wake.Set();
            Log.Warn("运动", "急停：所有轴的目标位置已锁定为当前位置");
            FireUpdated();
        }

        // ─────────────────────────── 内部 ───────────────────────────

        private Axis GetAxis(string axis) => axis.Trim().ToUpperInvariant() switch
        {
            "X" => _x,
            "Y" => _y,
            "Z" => _z,
            _ => throw new ArgumentException($"未知轴：{axis}")
        };

        private void SetTarget(Axis a, double pos)
        {
            if (pos < a.Min - 1e-6 || pos > a.Max + 1e-6)
            {
                string msg = $"{a.Name} 轴目标 {pos:F2}mm 超出软限位 [{a.Min:F0},{a.Max:F0}]，已拦截";
                Log.Alarm("运动", msg);
                Alarm?.Invoke(msg);
                pos = Math.Clamp(pos, a.Min, a.Max);
            }
            lock (_sync) a.Target = pos;
        }

        private static bool IsAxisMoving(Axis a) => Math.Abs(a.Target - a.Pos) > 0.01;

        private async Task WaitIdleAsync(Axis a, CancellationToken ct)
        {
            while (IsAxisMoving(a))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(4, ct).ConfigureAwait(false);
            }
        }

        private void EnsureLoop()
        {
            if (_thread != null) return;
            _loop = true;
            _lastT = _clock.Elapsed.TotalSeconds;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SVS.MotionSim", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        private void Loop()
        {
            while (_loop)
            {
                _wake.WaitOne(4);
                double t = _clock.Elapsed.TotalSeconds;
                double dt = t - _lastT;
                _lastT = t;
                if (dt <= 0 || dt > 0.2) dt = 0.004;

                bool moving;
                try { moving = Step(dt); }
                catch (Exception ex) { Log.Error("运动", "运动插补异常", ex); continue; }

                if (t - _lastNotify >= 0.04 || (_wasMoving && !moving))
                {
                    _lastNotify = t;
                    FireUpdated();
                }
                _wasMoving = moving;
            }
        }

        /// <summary>单步物理推进：梯形速度曲线（按剩余距离限制刹车速度，保证不过冲）</summary>
        private bool Step(double dt)
        {
            bool any = false;
            foreach (var a in new[] { _x, _y, _z })
            {
                double dist = a.Target - a.Pos;
                double remain = Math.Abs(dist);
                if (remain <= 1e-3) { a.Pos = a.Target; a.Vel = 0; continue; }

                int dir = Math.Sign(dist);
                double brakeVel = Math.Sqrt(2 * a.Acc * remain);   // 以当前加速度能在剩余距离内刹停的速度
                double vGoal = Math.Min(a.MaxVel, brakeVel);
                a.Vel = Math.Min(vGoal, a.Vel + a.Acc * dt);
                if (a.Vel < 1.0) a.Vel = 1.0;                      // 起步不粘滞

                double stepLen = a.Vel * dt;
                if (stepLen >= remain)
                {
                    a.Pos = a.Target;
                    a.Vel = 0;
                }
                else
                {
                    a.Pos += dir * stepLen;
                    any = true;
                }
            }
            return any;
        }

        private void FireUpdated()
        {
            try { Updated?.Invoke(new MotionSnapshot(_x.Pos, _y.Pos, _z.Pos, IsMoving, _isHomed)); }
            catch (Exception ex) { Log.Error("运动", "位置事件回调异常", ex); }
        }

        public void Dispose()
        {
            _loop = false;
            _wake.Set();
            try { _thread?.Join(300); } catch { }
            _thread = null;
            _wake.Dispose();
        }
    }
}
