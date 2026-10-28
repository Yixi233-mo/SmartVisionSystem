using System.Runtime.InteropServices;
using SmartVisionSystem.Core;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.UI;

namespace SmartVisionSystem
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        private static int Main(string[] args)
        {
            // ── 无头自检：不启动界面，直接验证视觉/运动/数据库链路 ──
            if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                AttachConsole(-1);
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                return SelfTest.Run();
            }

            Log.Init();
            WindowStyle.EnableProcessDarkMode();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            Application.EnableVisualStyles();

            Application.SetCompatibleTextRenderingDefault(false);

            // 界面快照回归：--shot <目录>
            var shotIdx = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
            if (shotIdx >= 0)
            {
                var dir = shotIdx + 1 < args.Length ? args[shotIdx + 1] : Path.Combine(AppPaths.LogDir, "shots");
                return UiSnapshot.Run(dir);
            }

            // 全局未处理异常：落盘 + 友好提示，绝不让进程"静默消失"
            Application.ThreadException += (_, e) => HandleCrash("UI线程", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleCrash("后台线程", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => { HandleCrash("任务", e.Exception); e.SetObserved(); };

            try
            {
                Log.Info("系统", "======== 程序启动 ========");

                // --auto：跳过登录（现场调机/截图/自动化验收用），默认以设备工程师身份进入
                if (args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase)))
                {
                    Auth.LoginAs(UserRole.Engineer);
                }
                else
                {
                    using var login = new LoginForm();
                    if (login.ShowDialog() != DialogResult.OK)
                    {
                        Log.Info("系统", "用户取消登录，程序退出");
                        Log.Shutdown();
                        return 0;
                    }
                }

                // 登录后才初始化硬件（避免无权限用户触发设备动作）
                DeviceHub.I.Initialize();

                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                HandleCrash("启动", ex);
                return 1;
            }
            finally
            {
                try { if (DeviceHub.I.IsInitialized) DeviceHub.I.Dispose(); } catch { }
                Log.Shutdown();
            }
        }

        private static void HandleCrash(string source, Exception? ex)
        {
            Log.Error("系统", $"未处理异常（{source}）", ex);
            try
            {
                MessageBox.Show(
                    $"程序遇到未处理的异常（{source}）：\n\n{ex?.Message}\n\n详细信息已写入 logs 目录，请将日志提供给设备厂商。",
                    "系统异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
