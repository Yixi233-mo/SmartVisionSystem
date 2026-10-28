namespace SmartVisionSystem.Hardware
{
    /// <summary>
    /// 模拟"网线被拔 / 交换机掉电"：所有设备（相机、运动卡、PLC）共享这一条通讯链路。
    /// 断线期间设备访问一律抛 DeviceCommException，业务层必须转入报警态并在恢复后自动重连——
    /// 这正是非功能需求里"通信断线重连，禁止闪退"的落地实现。
    /// </summary>
    public sealed class NetworkLink
    {
        private volatile bool _isUp = true;
        private CancellationTokenSource? _restoreCts;

        public bool IsUp => _isUp;
        public event Action<bool>? StateChanged;

        public void DropFor(int durationMs)
        {
            if (!_isUp) return;
            _isUp = false;
            Infrastructure.Log.Alarm("通讯", $"网络链路中断（模拟拔线），{durationMs} ms 后自动恢复");
            StateChanged?.Invoke(false);

            _restoreCts?.Cancel();
            _restoreCts = new CancellationTokenSource();
            var token = _restoreCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Max(200, durationMs), token);
                    if (!token.IsCancellationRequested) Restore();
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        public void Restore()
        {
            if (_isUp) return;
            _isUp = true;
            Infrastructure.Log.Info("通讯", "网络链路恢复，设备开始自动重连");
            StateChanged?.Invoke(true);
        }

        /// <summary>设备访问前的连通性检查</summary>
        public void ThrowIfDown(string device)
        {
            if (!_isUp) throw new DeviceCommException(device, $"{device} 通讯中断：网络链路不可达");
        }
    }
}
