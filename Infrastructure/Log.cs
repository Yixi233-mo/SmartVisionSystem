using System.Collections.Concurrent;
using System.Text;

namespace SmartVisionSystem.Infrastructure
{
    public enum LogLevel { Debug, Info, Warn, Error, Alarm }

    public sealed record LogEntry(DateTime Time, LogLevel Level, string Message, string? Exception);

    /// <summary>
    /// 轻量级异步日志：按天滚动落盘 logs/yyyy-MM-dd.log，不依赖第三方包。
    /// 生产者（UI 线程 / 工位线程 / 硬件线程）只入队，写盘由单一后台线程完成，避免磁盘 IO 抖动影响节拍。
    /// </summary>
    public static class Log
    {
        private static readonly BlockingCollection<LogEntry> _queue = new(new ConcurrentQueue<LogEntry>(), 20000);
        private static Thread? _worker;
        private static readonly object _sync = new();
        private static volatile bool _started;
        private static volatile bool _shutdown;
        private static readonly StringBuilder _sb = new(256);

        /// <summary>日志写入事件（UI 底部滚动条订阅，注意在 UI 线程 marshal）</summary>
        public static event Action<LogEntry>? EntryWritten;

        public static void Init()
        {
            lock (_sync)
            {
                if (_started || _shutdown) return;
                _started = true;
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "SVS.LogWriter" };
                _worker.Start();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            }
            Info("系统", "日志子系统启动");
        }

        private static void WorkerLoop()
        {
            StreamWriter? writer = null;
            DateTime currentDate = DateTime.MinValue;
            try
            {
                foreach (var entry in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (writer == null || entry.Time.Date != currentDate)
                        {
                            writer?.Flush();
                            writer?.Dispose();
                            currentDate = entry.Time.Date;
                            var file = Path.Combine(AppPaths.LogDir, $"{currentDate:yyyy-MM-dd}.log");
                            writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                        }

                        _sb.Clear();
                        _sb.Append(entry.Time.ToString("HH:mm:ss.fff")).Append(" [").Append(entry.Level.ToString().ToUpperInvariant().PadRight(5)).Append("] ")
                           .Append(entry.Message);
                        if (!string.IsNullOrEmpty(entry.Exception))
                            _sb.AppendLine().Append("        ").Append(entry.Exception!.Replace("\n", "\n        "));
                        writer.WriteLine(_sb.ToString());
                    }
                    catch
                    {
                        // 日志自身异常绝不允许影响主业务
                    }

                    try { EntryWritten?.Invoke(entry); } catch { }
                }
            }
            finally
            {
                try { writer?.Flush(); writer?.Dispose(); } catch { }
            }
        }

        public static void Write(LogLevel level, string message, string? exception = null)
        {
            var entry = new LogEntry(DateTime.Now, level, message, exception);
            if (!_started) { Init(); }
            if (_shutdown) return;
            if (!_queue.IsAddingCompleted) _queue.TryAdd(entry);
        }

        public static void Debug(string msg) => Write(LogLevel.Debug, msg);
        public static void Info(string tag, string msg) => Write(LogLevel.Info, $"[{tag}] {msg}");
        public static void Warn(string tag, string msg) => Write(LogLevel.Warn, $"[{tag}] {msg}");
        public static void Alarm(string tag, string msg) => Write(LogLevel.Alarm, $"[{tag}] {msg}");
        public static void Error(string tag, string msg, Exception? ex = null) => Write(LogLevel.Error, $"[{tag}] {msg}", ex?.ToString());

        /// <summary>刷盘并停止（退出时调用，确保最后几条日志不丢）</summary>
        public static void Shutdown()
        {
            lock (_sync)
            {
                if (!_started) return;
                _started = false;
                _shutdown = true;
                _queue.CompleteAdding();
                try { _worker?.Join(1500); } catch { }
                _worker = null;
            }
        }
    }
}
