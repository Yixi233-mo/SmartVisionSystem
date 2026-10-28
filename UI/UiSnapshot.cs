using SmartVisionSystem.Core;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem.UI
{
    /// <summary>
    /// 界面快照回归（dotnet run -- --shot <输出目录>）：
    /// 逐页渲染主界面并存成 PNG，用于交付验收与改动后的布局回归比对，不需要人工盯着屏幕点。
    /// </summary>
    public static class UiSnapshot
    {
        public static int Run(string outDir)
        {
            try
            {
                Directory.CreateDirectory(outDir);

                // 先拍登录页（此时尚未登录）
                using (var login = new LoginForm())
                {
                    // FixedDialog 边框下 DrawToBitmap 的非客户区偏移不准，会吃掉客户区顶部约 70px，
                    // 截图前临时去掉边框，保证像素级还原客户区
                    login.FormBorderStyle = FormBorderStyle.None;
                    login.Show();
                    Pump(600);
                    using var loginBmp = new Bitmap(login.Width, login.Height);
                    login.DrawToBitmap(loginBmp, new Rectangle(0, 0, login.Width, login.Height));
                    loginBmp.Save(Path.Combine(outDir, "00-登录页.png"), System.Drawing.Imaging.ImageFormat.Png);
                    login.Hide();
                }

                Auth.LoginAs(UserRole.Admin);
                DeviceHub.I.Initialize();
                Thread.Sleep(700); // 等首帧预览图

                var form = new MainForm
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(0, 0),
                    ShowInTaskbar = false,
                    WindowState = FormWindowState.Normal
                };
                form.Show();

                var pages = new (string Name, Type Type)[]
                {
                    ("01-生产监控", typeof(MonitorView)),
                    ("02-运动控制", typeof(MotionView)),
                    ("03-参数与配方", typeof(SettingsView)),
                    ("04-历史数据", typeof(HistoryView)),
                    ("05-产线仿真", typeof(SimulatorView)),
                    ("06-用户管理", typeof(UserManageView)),
                    ("06-用户管理", typeof(UserManageView)),
                };

                foreach (var (name, type) in pages)
                {
                    form.ShowView(type);
                    Pump(900);
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    var path = Path.Combine(outDir, name + ".png");
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Log.Info("快照", $"已保存 {path}");
                }

                // 权限裁剪验证：换成设备工程师后，导航里不应再出现"用户管理"
                Auth.LoginAs(UserRole.Engineer);
                form.ReloadNavForCurrentUser();
                Pump(700);
                using (var bmp2 = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp2, new Rectangle(0, 0, form.Width, form.Height));
                    bmp2.Save(Path.Combine(outDir, "07-工程师视角.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Log.Info("快照", "工程师可见导航：" + string.Join(" / ", form.NavTexts));

                // 权限裁剪验证：换成设备工程师后，导航里不应再出现"用户管理"
                Auth.LoginAs(UserRole.Engineer);
                form.ReloadNavForCurrentUser();
                Pump(700);
                using (var bmp2 = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp2, new Rectangle(0, 0, form.Width, form.Height));
                    bmp2.Save(Path.Combine(outDir, "07-工程师视角.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Log.Info("快照", "工程师可见导航：" + string.Join(" / ", form.NavTexts));

                form.Close();
                DeviceHub.I.Dispose();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("快照", "界面快照失败", ex);
                return 1;
            }
        }

        private static void Pump(int ms)
        {
            var until = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < until)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }
    }
}
