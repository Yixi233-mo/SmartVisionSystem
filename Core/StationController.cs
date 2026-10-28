using System.Diagnostics;
using OpenCvSharp.Extensions;
using SmartVisionSystem.Hardware;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.Simulation;

namespace SmartVisionSystem.Core
{
    public enum StationState { Idle, Homing, Ready, Running, Stopped, Alarm }

    public enum StationPhase { None, 等待来料, 相机拍照, 视觉识别, 计算纠偏, 取放料, 数据入库 }

    public sealed class FrameReadyEventArgs : EventArgs
    {
        public required Bitmap Image { get; init; }
        public VisionResult? Result { get; init; }
        public string SN { get; init; } = "";
        public Recipe? Recipe { get; init; }
    }

    public sealed class CycleCompletedEventArgs : EventArgs
    {
        public required DetectRecord Record { get; init; }
        public required VisionResult Result { get; init; }
        public required string SN { get; init; }
        public required LinePart Part { get; init; }
    }

    /// <summary>
    /// 工位主控（整个系统的"大脑"）：由独立线程驱动一个完整生产循环——
    /// 等料 → 触发拍照 → 视觉识别 → 偏差计算 → 机械手纠偏取放 → 判定 → 入库 → 回传 PLC → 放行。
    ///
    /// 工程要点：
    /// · 全部硬件访问都在本线程，UI 只订阅事件，避免 UI 卡顿拖慢节拍；
    /// · 任一设备通讯异常立即转入报警态并停止输出，链路恢复后自动重连续跑；
    /// · 参数合法性在启动与每拍开始前双重校验（防呆）。
    /// </summary>
    public sealed class StationController : IDisposable
    {
        private readonly VirtualProductionLine _line;
        private readonly SimCamera _camera;
        private readonly SimMotionController _motion;
        private readonly SimPlc _plc;
        private readonly DatabaseManager _db;
        private readonly NetworkLink _net;

        private Thread? _thread;
        private volatile bool _loop;
        private readonly AutoResetEvent _signal = new(false);
        private CancellationTokenSource _cts = new();
        private readonly object _inspectLock = new();

        private volatile StationState _state = StationState.Idle;
        private volatile StationPhase _phase = StationPhase.None;
        private volatile bool _resumeAfterRecovery;

        public StationController(VirtualProductionLine line, SimCamera camera, SimMotionController motion,
                                 SimPlc plc, DatabaseManager db, NetworkLink net)
        {
            _line = line;
            _camera = camera;
            _motion = motion;
            _plc = plc;
            _db = db;
            _net = net;

            _net.StateChanged += OnNetworkChanged;
        }

        // ─────────────────────────── 对外状态 ───────────────────────────

        public StationState State => _state;
        public StationPhase Phase => _phase;
        public string LastAlarm { get; private set; } = "";
        public bool IsRunning => _state == StationState.Running;
        public bool PreviewEnabled { get; set; } = true;
        public StationStatistics Stats { get; } = new();

        public event Action<StationState>? StateChanged;
        public event Action<StationPhase>? PhaseChanged;
        public event Action<FrameReadyEventArgs>? FrameReady;
        public event Action<CycleCompletedEventArgs>? CycleCompleted;
        public event Action<string, bool>? AlarmRaised;
        public event Action<string>? Message;

        // ─────────────────────────── 生命周期 ───────────────────────────

        public void Initialize()
        {
            _loop = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SVS.Station", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            SetState(StationState.Ready);
            Log.Info("工位", "工位主控线程启动，当前状态：就绪");
        }

        /// <summary>启动生产（含自动回零）</summary>
        public bool Start(out string error)
        {
            error = "";
            if (_state == StationState.Running) return true;

            var recipe = RecipeStore.Current;
            if (!recipe.Validate(out error))
            {
                Log.Warn("工位", $"启动被拒绝：{error}");
                AlarmRaised?.Invoke($"启动被拒绝：{error}", false);
                return false;
            }
            if (!_net.IsUp)
            {
                error = "设备通讯中断，无法启动";
                return false;
            }
            if (!_camera.IsConnected)
            {
                error = "相机未连接，无法启动";
                return false;
            }

            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            _resumeAfterRecovery = true;
            SetState(StationState.Running);
            SetPhase(StationPhase.等待来料);
            _plc.Write(PlcTags.SystemRun, true);
            _line.Start();
            _plc.Write(PlcTags.LineRun, true);
            _signal.Set();

            Log.Info("工位", $"生产启动（配方：{recipe.Name}，线速 {_line.Settings.LineSpeedMmS:F0}mm/s）");
            Message?.Invoke("生产已启动");
            return true;
        }

        /// <summary>停止生产（含急停语义：立即锁定所有轴）</summary>
        public void Stop(string reason = "操作员停止")
        {
            if (_state is StationState.Stopped or StationState.Idle && _line.State == LineState.Stopped)
            {
                // 仍允许干净的重复停止
            }

            _resumeAfterRecovery = false;
            try { _cts.Cancel(); } catch { }

            _motion.StopAll();
            _line.Stop();
            _plc.Write(PlcTags.SystemRun, false);
            _plc.Write(PlcTags.LineRun, false);
            _plc.Write(PlcTags.VisionDone, false);
            SetPhase(StationPhase.None);
            SetState(StationState.Stopped);

            Log.Warn("工位", $"生产停止（{reason}）");
            Message?.Invoke($"生产已停止：{reason}");
        }

        /// <summary>报警复位：清报警、重建设备连接，回到就绪</summary>
        public bool ResetAlarm(out string error)
        {
            error = "";
            try
            {
                _camera.Open();
                _motion.Open();
                _plc.Open();
                LastAlarm = "";
                if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
                SetState(StationState.Ready);
                Log.Info("工位", "报警已复位，设备重新就绪");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("工位", "报警复位失败", ex);
                return false;
            }
        }

        /// <summary>单次检测（不动机器人）：工程师校验阈值/模板时使用</summary>
        public VisionResult? InspectOnce(out string error)
        {
            error = "";
            if (_state == StationState.Running)
            {
                error = "生产运行中不允许单次检测";
                return null;
            }
            var recipe = RecipeStore.Current;
            if (!recipe.HasTemplate) { error = "尚未建立模板"; return null; }

            lock (_inspectLock)
            {
                try
                {
                    _camera.ExposureUs = recipe.ExposureUs;
                    _camera.GainDb = recipe.GainDb;
                    _camera.PixelsPerMm = recipe.PixelsPerMm;
                    using var frame = _camera.GrabFrame();
                    var result = VisionEngine.Inspect(frame, recipe);
                    RaiseFrame(frame, result, "SINGLE", recipe);
                    Log.Info("工位", $"单次检测：{(result.IsOk ? "OK" : "NG " + result.NgReason)}，得分 {result.Score:F3}，CT {result.CtMs:F1}ms");
                    return result;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Error("工位", "单次检测失败", ex);
                    return null;
                }
            }
        }

        // ─────────────────────────── 主循环 ───────────────────────────

        private void Loop()
        {
            while (_loop)
            {
                try
                {
                    if (_state != StationState.Running)
                    {
                        PreviewTick();
                        _signal.WaitOne(70);
                        continue;
                    }

                    if (!_motion.IsHomed)
                    {
                        SetState(StationState.Homing);
                        SetPhase(StationPhase.None);
                        Log.Info("工位", "启动前置条件：执行全轴回零");
                        _motion.HomeAllAsync(_cts.Token).GetAwaiter().GetResult();
                        SetState(StationState.Running);
                        SetPhase(StationPhase.等待来料);
                    }

                    SetPhase(StationPhase.等待来料);
                    var part = _line.TakeStationPart(200);
                    if (part == null) continue;

                    RunCycle(part);
                }
                catch (OperationCanceledException)
                {
                    // 正常停止路径
                }
                catch (DeviceCommException ex)
                {
                    EnterAlarm($"{ex.Device} 通讯异常：{ex.Message}");
                }
                catch (Exception ex)
                {
                    Log.Error("工位", "生产循环异常", ex);
                    EnterAlarm($"工位异常：{ex.Message}");
                }
            }
        }

        private void PreviewTick()
        {
            if (!PreviewEnabled) return;
            lock (_inspectLock)
            {
                try
                {
                    var recipe = RecipeStore.Current;
                    _camera.PixelsPerMm = recipe.PixelsPerMm;
                    using var frame = _camera.GrabFrame();
                    RaiseFrame(frame, null, "PREVIEW", recipe);
                }
                catch (DeviceCommException) { /* 断线时预览静默失败，由链路事件负责报警 */ }
                catch (Exception ex) { Log.Debug($"预览失败：{ex.Message}"); }
            }
        }

        private void RunCycle(LinePart part)
        {
            var recipe = RecipeStore.Current;
            if (!recipe.Validate(out var err))
            {
                EnterAlarm($"配方不可用：{err}");
                _line.Release(part);
                return;
            }

            var cycleSw = Stopwatch.StartNew();

            // ① 配方参数下发给相机
            _camera.ExposureUs = recipe.ExposureUs;
            _camera.GainDb = recipe.GainDb;
            _camera.PixelsPerMm = recipe.PixelsPerMm;

            // ② 拍照
            SetPhase(StationPhase.相机拍照);
            _plc.Write(PlcTags.PartArrived, true);
            _plc.Write(PlcTags.RobotBusy, true);
            _plc.Pulse(PlcTags.TriggerOut, 30);

            using var frame = _camera.GrabFrame();
            _plc.Write(PlcTags.PartArrived, false);

            // ③ 视觉识别 + 偏差计算
            SetPhase(StationPhase.视觉识别);
            var result = VisionEngine.Inspect(frame, recipe);
            RaiseFrame(frame, result, part.SN, recipe);

            bool ok = result.IsOk;
            part.InspectedOk = ok;
            double tx = recipe.RobotBaseX + result.OffsetXmm;
            double ty = recipe.RobotBaseY + result.OffsetYmm;

            // ④ 纠偏取放料（NG 件同样取走，投入 NG 料道，避免产线堵塞）
            SetPhase(ok ? StationPhase.计算纠偏 : StationPhase.取放料);
            double px = ok ? recipe.PlaceX : recipe.PlaceNgX;
            double py = ok ? recipe.PlaceY : recipe.PlaceNgY;

            var ct = _cts.Token;
            _motion.MoveToAsync(tx, ty, recipe.ZSafe, ct).GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            _motion.MoveAbsoluteAsync("Z", recipe.ZPick, ct).GetAwaiter().GetResult();
            _motion.MoveAbsoluteAsync("Z", recipe.ZSafe, ct).GetAwaiter().GetResult();
            _motion.MoveToAsync(px, py, null, ct).GetAwaiter().GetResult();
            _motion.MoveAbsoluteAsync("Z", recipe.ZPlace, ct).GetAwaiter().GetResult();
            _motion.MoveAbsoluteAsync("Z", recipe.ZSafe, ct).GetAwaiter().GetResult();
            _motion.MoveToAsync(recipe.RobotBaseX, recipe.RobotBaseY, null, ct).GetAwaiter().GetResult();

            double cycleMs = cycleSw.Elapsed.TotalMilliseconds;

            // ⑤ 判定回传 + 入库
            SetPhase(StationPhase.数据入库);
            _plc.Write(PlcTags.ResultOk, ok);
            _plc.Write(PlcTags.ResultNg, !ok);
            _plc.Pulse(PlcTags.VisionDone, 60);

            var record = new DetectRecord
            {
                Time = DateTime.Now,
                SN = part.SN,
                Recipe = recipe.Name,
                Result = ok ? "OK" : "NG",
                OffsetX = result.OffsetXmm,
                OffsetY = result.OffsetYmm,
                OffsetXPx = result.OffsetXPx,
                OffsetYPx = result.OffsetYPx,
                Score = result.Score,
                Angle = result.AngleDeg,
                Area = result.MaterialAreaPx,
                SurfaceRatio = result.SurfaceRatio,
                CtMs = result.CtMs,
                CycleMs = cycleMs,
                NgReason = result.NgReason
            };
            _db.Insert(record);
            Stats.Add(ok, result.CtMs, cycleMs);

            _plc.Write(PlcTags.RobotBusy, false);

            Log.Info("检测", $"{part.SN} {(ok ? "OK" : "NG")} | 得分 {result.Score:F3} | 偏差 ({result.OffsetXmm:+0.00;-0.00},{result.OffsetYmm:+0.00;-0.00})mm | CT {result.CtMs:F1}ms | 节拍 {cycleMs:F0}ms" +
                             (ok ? "" : $" | 原因：{result.NgReason}"));

            if (cycleMs > recipe.MaxCycleMs)
            {
                string msg = $"{part.SN} 节拍 {cycleMs:F0}ms 超出上限 {recipe.MaxCycleMs:F0}ms";
                Log.Warn("工位", msg);
                AlarmRaised?.Invoke(msg, false);
            }

            CycleCompleted?.Invoke(new CycleCompletedEventArgs { Record = record, Result = result, SN = part.SN, Part = part });
            SetPhase(StationPhase.等待来料);

            _line.Release(part);
        }

        // ─────────────────────────── 内部 ───────────────────────────

        private void RaiseFrame(OpenCvSharp.Mat frame, VisionResult? result, string sn, Recipe recipe)
        {
            try
            {
                var bmp = BitmapConverter.ToBitmap(frame);
                FrameReady?.Invoke(new FrameReadyEventArgs { Image = bmp, Result = result, SN = sn, Recipe = recipe });
            }
            catch (Exception ex)
            {
                Log.Error("工位", "图像显示分发失败", ex);
            }
        }

        private void EnterAlarm(string message)
        {
            LastAlarm = message;
            _plc.Write(PlcTags.SystemRun, false);
            _plc.Write(PlcTags.LineRun, false);
            _line.Stop();
            try { _motion.StopAll(); } catch { }
            SetState(StationState.Alarm);
            Log.Alarm("工位", message);
            AlarmRaised?.Invoke(message, true);
        }

        private void OnNetworkChanged(bool up)
        {
            if (up)
            {
                if (_state != StationState.Alarm) return;
                Log.Info("工位", "通讯恢复，尝试自动重连并恢复生产");
                Task.Run(() =>
                {
                    try
                    {
                        _camera.Open();
                        _motion.Open();
                        _plc.Open();
                        LastAlarm = "";
                        if (_resumeAfterRecovery)
                        {
                            SetState(StationState.Running);
                            _line.Start();
                            _plc.Write(PlcTags.SystemRun, true);
                            _signal.Set();
                            Message?.Invoke("通讯恢复，生产已自动继续");
                        }
                        else
                        {
                            SetState(StationState.Ready);
                            Message?.Invoke("通讯恢复，设备已重连");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("工位", "自动重连失败", ex);
                    }
                });
            }
            else
            {
                _resumeAfterRecovery = _state == StationState.Running;
                EnterAlarm("设备通讯中断（模拟网线断开），产线已停止");
            }
        }

        private void SetState(StationState s)
        {
            if (_state == s) return;
            _state = s;
            try { StateChanged?.Invoke(s); } catch { }
        }

        private void SetPhase(StationPhase p)
        {
            if (_phase == p) return;
            _phase = p;
            try { PhaseChanged?.Invoke(p); } catch { }
        }

        public void Dispose()
        {
            _loop = false;
            try { _cts.Cancel(); } catch { }
            _signal.Set();
            try { _thread?.Join(800); } catch { }
            _thread = null;
            _net.StateChanged -= OnNetworkChanged;
            _signal.Dispose();
        }
    }
}
