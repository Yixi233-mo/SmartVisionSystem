using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.Simulation
{
    public enum LineState { Stopped, Running, Holding }

    /// <summary>
    /// 虚拟产线（间歇定位式输送线）：以物理量（mm）为单位推进工件，工件走到相机工位后停线、发触发信号，
    /// 工位完成取放料后放行。这是"没有真实产线也能演示视觉引导闭环"的核心仿真对象。
    ///
    /// 为什么用"停位式"而不是"飞拍式"：真实 3C 产线多数采用间歇定位（Indexing）输送，停稳后拍照，
    /// 视觉偏差用于机械手纠偏；该模型能真实体现"来料位置不固定 → 视觉定位"的业务价值。
    /// </summary>
    public sealed class VirtualProductionLine : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<LinePart> _parts = new();
        private readonly SimulatorSettings _settings;
        private readonly ManualResetEventSlim _stopSignal = new(false);

        private Thread? _thread;
        private volatile bool _loop;
        private int _snSeq;

        /// <summary>相机工位在线体坐标中的位置（mm）</summary>
        public double StationMm { get; } = 2000;

        public LineState State { get; private set; } = LineState.Stopped;

        /// <summary>皮带累计位移（mm），用于画面纹理滚动</summary>
        public double BeltPhaseMm { get; private set; }

        public event Action<LinePart>? PartArrived;
        public event Action<LineState>? StateChanged;

        public VirtualProductionLine(SimulatorSettings settings)
        {
            _settings = settings;
            // 上电即在线上预置待检件：现实中线体上本来就停着料，也让操作员启动前就能看到画面
            lock (_sync) RefillLocked();
        }

        public SimulatorSettings Settings => _settings;

        // ─────────────────────────── 生命周期 ───────────────────────────

        public void Start()
        {
            if (_thread == null)
            {
                _loop = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "SVS.LineSim", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
            lock (_sync)
            {
                if (_parts.Count == 0) RefillLocked();
                if (State == LineState.Stopped)
                {
                    State = LineState.Running;
                    Monitor.PulseAll(_sync);
                }
            }
            Log.Info("产线", $"输送启动（线速 {_settings.LineSpeedMmS:F0}mm/s，间距 {_settings.PartPitchMm:F0}mm，在线 {_parts.Count} 件）");
            StateChanged?.Invoke(State);
        }

        /// <summary>停止输送（工件保持当前位置）</summary>
        public void Stop()
        {
            lock (_sync)
            {
                if (State == LineState.Stopped) return;
                State = LineState.Stopped;
                Monitor.PulseAll(_sync);
            }
            Log.Info("产线", "输送已停止");
            StateChanged?.Invoke(State);
        }

        /// <summary>工位处理完成，放行当前工件</summary>
        public void Release(LinePart part)
        {
            lock (_sync)
            {
                part.Stage = PartStage.Done;
                part.InspectTime = DateTime.Now;
                if (State == LineState.Holding)
                {
                    State = LineState.Running;
                    Monitor.PulseAll(_sync);
                }
            }
            StateChanged?.Invoke(State);
        }

        /// <summary>复位：清空在线工件重建队列（换线/清线）</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _parts.Clear();
                BeltPhaseMm = 0;
                State = LineState.Stopped;
                _snSeq = 0;
                Monitor.PulseAll(_sync);
            }
            Log.Info("产线", "产线已复位（清线）");
            StateChanged?.Invoke(State);
        }

        // ─────────────────────────── 查询 ───────────────────────────

        public IReadOnlyList<LinePart> Snapshot()
        {
            lock (_sync) return _parts.ToArray();
        }

        /// <summary>阻塞等待停在工位的工件（工位线程调用）</summary>
        public LinePart? TakeStationPart(int timeoutMs)
        {
            lock (_sync)
            {
                long deadline = Environment.TickCount64 + timeoutMs;
                while (true)
                {
                    var p = _parts.FirstOrDefault(x => x.Stage == PartStage.AtStation);
                    if (p != null)
                    {
                        p.Stage = PartStage.Inspecting;
                        return p;
                    }
                    int remain = (int)(deadline - Environment.TickCount64);
                    if (remain <= 0) return null;
                    Monitor.Wait(_sync, Math.Min(remain, 100));
                }
            }
        }

        // ─────────────────────────── 仿真循环 ───────────────────────────

        private void Loop()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double last = 0;
            while (_loop)
            {
                _stopSignal.Wait(20);
                double now = sw.Elapsed.TotalSeconds;
                double dt = now - last;
                last = now;
                if (dt <= 0 || dt > 0.5) dt = 0.02;

                try { Tick(dt); } catch (Exception ex) { Log.Error("产线", "仿真循环异常", ex); }
            }
        }

        private void Tick(double dt)
        {
            lock (_sync)
            {
                if (State != LineState.Running) return;

                double step = _settings.LineSpeedMmS * dt;
                if (step <= 0) return;

                BeltPhaseMm += step;
                foreach (var p in _parts)
                {
                    if (p.Stage is PartStage.Done)
                        p.PositionMm += step;
                    else if (p.Stage is PartStage.Queueing or PartStage.AtStation)
                        p.PositionMm += step;
                }

                // 最先到达工位的一件停下并触发
                var front = _parts.Where(p => p.Stage == PartStage.Queueing)
                                  .OrderBy(p => p.PositionMm)
                                  .FirstOrDefault();
                if (front != null && front.PositionMm >= StationMm)
                {
                    front.PositionMm = StationMm;
                    front.Stage = PartStage.AtStation;
                    State = LineState.Holding;
                    Monitor.PulseAll(_sync);
                    PartArrived?.Invoke(front);
                    return;
                }

                // 流出视野后移除，并补料
                _parts.RemoveAll(p => p.Stage == PartStage.Done && p.PositionMm > StationMm + _settings.PartPitchMm * 1.2);
                if (_parts.Count < Math.Max(1, _settings.QueueLength)) RefillLocked();
            }
        }

        private void RefillLocked()
        {
            if (_parts.Count == 0)
            {
                // 首件放在工位前一段距离，启动后很快到达（不用等满一整个间距）
                SpawnLocked(StationMm - _settings.PartPitchMm * 0.55);
                for (int i = 1; i < Math.Max(1, _settings.QueueLength); i++)
                    SpawnLocked(StationMm - _settings.PartPitchMm * (0.55 + i));
                return;
            }

            while (_parts.Count < Math.Max(1, _settings.QueueLength))
            {
                double last = _parts.Min(p => p.PositionMm);
                SpawnLocked(last - _settings.PartPitchMm);
            }
        }

        private void SpawnLocked(double positionMm)
        {
            _snSeq++;
            var s = _settings;
            bool defect = !s.PauseDefectInjection && Random.Shared.NextDouble() * 100 < Math.Clamp(s.DefectRatePct, 0, 100);
            var kind = defect ? PickDefect(s) : DefectKind.None;

            double ampX = s.OffsetAmpPx;
            double ampY = s.OffsetAmpPx * 0.75;
            double ampA = s.AngleAmpDeg;

            // 极端件：把偏差放大 4 倍，用来验证"超出搜索窗口 → 定位失败 → 报警"的防呆链路
            bool outlier = s.EnableOutlier && Random.Shared.NextDouble() < 0.05;
            if (outlier) { ampX *= 4; ampY *= 3; ampA = 22; }

            var part = new LinePart
            {
                SN = $"PV{DateTime.Now:yyMMdd}{_snSeq:D4}",
                PositionMm = positionMm,
                OffsetXPx = (Random.Shared.NextDouble() * 2 - 1) * ampX,
                OffsetYPx = (Random.Shared.NextDouble() * 2 - 1) * ampY,
                AngleDeg = (Random.Shared.NextDouble() * 2 - 1) * ampA,
                Defect = kind,
                Seed = Random.Shared.Next(1, int.MaxValue)
            };
            _parts.Add(part);
        }

        private static DefectKind PickDefect(SimulatorSettings s)
        {
            var pool = new List<DefectKind>(4);
            if (s.EnableMissingHole) pool.Add(DefectKind.MissingHole);
            if (s.EnableChip) pool.Add(DefectKind.Chip);
            if (s.EnableExtraMaterial) pool.Add(DefectKind.ExtraMaterial);
            if (s.EnableScratch) pool.Add(DefectKind.Scratch);
            if (pool.Count == 0) return DefectKind.None;
            return pool[Random.Shared.Next(pool.Count)];
        }

        public void Dispose()
        {
            _loop = false;
            _stopSignal.Set();
            try { _thread?.Join(500); } catch { }
            _thread = null;
            _stopSignal.Dispose();
        }
    }
}
