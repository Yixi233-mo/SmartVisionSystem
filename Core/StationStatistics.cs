namespace SmartVisionSystem.Core
{
    /// <summary>
    /// 生产统计：总量 / 良率 / 节拍。UI 通过 Clone() 拿快照，避免跨线程读写竞争。
    /// </summary>
    public sealed class StationStatistics
    {
        private readonly object _sync = new();

        public long Total { get; private set; }
        public long Ok { get; private set; }
        public long Ng { get; private set; }

        public double LastCtMs { get; private set; }
        public double AvgCtMs { get; private set; }
        public double LastCycleMs { get; private set; }
        public double AvgCycleMs { get; private set; }
        public double MaxCycleMs { get; private set; }

        public DateTime StartTime { get; private set; } = DateTime.Now;

        public double YieldPct
        {
            get
            {
                lock (_sync) return Total > 0 ? Ok * 100.0 / Total : 0;
            }
        }

        /// <summary>理论产能（件/小时），按平均节拍折算</summary>
        public double Uph
        {
            get
            {
                lock (_sync) return AvgCycleMs > 1 ? 3600000.0 / AvgCycleMs : 0;
            }
        }

        public void Add(bool isOk, double ctMs, double cycleMs)
        {
            lock (_sync)
            {
                Total++;
                if (isOk) Ok++; else Ng++;
                LastCtMs = ctMs;
                LastCycleMs = cycleMs;
                AvgCtMs += (ctMs - AvgCtMs) / Total;
                AvgCycleMs += (cycleMs - AvgCycleMs) / Total;
                if (cycleMs > MaxCycleMs) MaxCycleMs = cycleMs;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                Total = Ok = Ng = 0;
                LastCtMs = AvgCtMs = LastCycleMs = AvgCycleMs = MaxCycleMs = 0;
                StartTime = DateTime.Now;
            }
        }

        public StationStatistics Clone()
        {
            lock (_sync)
            {
                return new StationStatistics
                {
                    Total = Total, Ok = Ok, Ng = Ng,
                    LastCtMs = LastCtMs, AvgCtMs = AvgCtMs,
                    LastCycleMs = LastCycleMs, AvgCycleMs = AvgCycleMs, MaxCycleMs = MaxCycleMs,
                    StartTime = StartTime
                };
            }
        }

        public override string ToString()
            => $"总数 {Total} / OK {Ok} / NG {Ng} / 良率 {YieldPct:F2}% / 平均节拍 {AvgCycleMs:F0}ms";
    }
}
