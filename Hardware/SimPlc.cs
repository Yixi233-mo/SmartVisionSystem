using System.Collections.Concurrent;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.Hardware
{
    /// <summary>
    /// 虚拟 PLC：用内存点位表模拟 IO 模块，用于演示"视觉 ↔ 产线"的握手时序。
    /// 现场对接时把 Read/Write 换成 Modbus TCP / S7 通讯即可，上层时序代码不用改。
    /// </summary>
    public sealed class SimPlc : IPlc
    {
        private readonly ConcurrentDictionary<string, bool> _io = new();
        private readonly NetworkLink _net;
        private bool _opened;

        public SimPlc(NetworkLink net)
        {
            _net = net;
            foreach (var tag in PlcTags.All) _io[tag] = false;
        }

        public string Name => "SIM-PLC (虚拟 IO 模块)";
        public bool IsConnected => _opened && _net.IsUp;

        public event Action<string, bool>? IoChanged;

        public void Open()
        {
            _opened = true;
            Log.Info("PLC", "虚拟 IO 模块已连接，点位表就绪");
        }

        public void Close()
        {
            _opened = false;
            Log.Info("PLC", "虚拟 IO 模块已断开");
        }

        public bool Read(string tag) => _io.TryGetValue(tag, out var v) && v;

        public void Write(string tag, bool value)
        {
            if (!_io.TryGetValue(tag, out var old) || old != value)
            {
                _io[tag] = value;
                IoChanged?.Invoke(tag, value);
            }
        }

        public void Pulse(string tag, int widthMs = 50)
        {
            Write(tag, true);
            _ = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(10, widthMs));
                Write(tag, false);
            });
        }

        public void Dispose() => Close();
    }
}
