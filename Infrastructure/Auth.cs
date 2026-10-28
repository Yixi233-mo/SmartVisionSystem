using System.Collections.Concurrent;

namespace SmartVisionSystem.Infrastructure
{
    public enum UserRole
    {
        Operator = 0,   // 操作员：只看生产监控 + 历史数据
        Engineer = 1,   // 设备工程师：可换线、改参数、调相机、点动轴
        Admin = 2       // 管理员：全权限（含用户管理）
    }

    public sealed class UserSession
    {
        public int UserId { get; init; }
        public string UserName { get; init; } = "guest";
        public string DisplayName { get; init; } = "未登录";
        public UserRole Role { get; init; } = UserRole.Operator;
        public DateTime LoginTime { get; init; } = DateTime.Now;

        /// <summary>权限判定：参数/运动/仿真类操作统一走这里，贯穿 UI 与业务层双保险</summary>
        public bool Can(Permission p) => p switch
        {
            Permission.OperateStation => true,                       // 启停/复位：操作员可用
            Permission.ViewHistory => true,
            Permission.EditRecipe => Role >= UserRole.Engineer,      // 改配方/模板：工程师起
            Permission.JogMotion => Role >= UserRole.Engineer,
            Permission.TuneSimulator => Role >= UserRole.Engineer,
            Permission.SimulateFault => Role >= UserRole.Engineer,
            Permission.ClearDatabase => Role >= UserRole.Admin,
            Permission.ManageUsers => Role >= UserRole.Admin,        // 用户管理：仅管理员
            _ => false
        };

        public override string ToString() => $"{DisplayName}({Role})";
    }

    public enum Permission
    {
        OperateStation,
        ViewHistory,
        EditRecipe,
        JogMotion,
        TuneSimulator,
        SimulateFault,
        ClearDatabase,
        ManageUsers
    }

    /// <summary>
    /// 登录会话与权限闸门。账号数据来自 <see cref="UserStore"/>（SQLite 持久化，可由管理员增删改）。
    /// 额外加了一道暴力破解防护：同一账号连续 5 次口令错误即锁定 60 秒。
    /// </summary>
    public static class Auth
    {
        private const int MaxFailBeforeLock = 5;
        private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);
        private static readonly ConcurrentDictionary<string, (int Fails, DateTime LockUntil)> _attempts = new(StringComparer.OrdinalIgnoreCase);

        static Auth()
        {
            try { UserStore.EnsureSeeded(); }
            catch (Exception ex) { Log.Error("用户", "初始化账号库失败", ex); }
        }

        public static UserSession Current { get; private set; } =
            new() { UserName = "guest", DisplayName = "未登录", Role = UserRole.Operator };

        /// <summary>账号锁定剩余秒数（0 表示未锁定）</summary>
        public static int LockedSeconds(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return 0;
            if (!_attempts.TryGetValue(userName.Trim(), out var st)) return 0;
            if (st.LockUntil <= DateTime.Now) return 0;
            return (int)Math.Ceiling((st.LockUntil - DateTime.Now).TotalSeconds);
        }

        public static bool TryLogin(string user, string password, out UserSession session, out string error)
        {
            session = Current;
            error = "";

            user = (user ?? "").Trim();
            if (user.Length == 0) { error = "请输入账号"; return false; }

            int locked = LockedSeconds(user);
            if (locked > 0)
            {
                error = $"账号已锁定，请 {locked} 秒后重试";
                return false;
            }

            if (!UserStore.Verify(user, password, out var account, out error) || account == null)
            {
                var st = _attempts.AddOrUpdate(user, _ => (1, DateTime.MinValue),
                    (_, old) => (old.Fails + 1, old.LockUntil));

                if (st.Fails >= MaxFailBeforeLock)
                {
                    _attempts[user] = (0, DateTime.Now + LockDuration);
                    Log.Alarm("登录", $"账号 {user} 连续 {MaxFailBeforeLock} 次口令错误，已锁定 {LockDuration.TotalSeconds:F0} 秒");
                    error = $"口令错误次数过多，账号锁定 {LockDuration.TotalSeconds:F0} 秒";
                }
                else
                {
                    Log.Warn("登录", $"登录失败：{user} — {error}（第 {st.Fails} 次）");
                }
                return false;
            }

            _attempts.TryRemove(user, out _);
            UserStore.RecordLogin(account.UserName);

            session = new UserSession
            {
                UserId = account.Id,
                UserName = account.UserName,
                DisplayName = account.DisplayName,
                Role = account.Role
            };
            Current = session;
            Log.Info("登录", $"{account.DisplayName}（{account.UserName}）登录成功，角色权限 = {account.Role}");
            return true;
        }

        /// <summary>免密进入（现场调机 / 自动运行 / 截图验收用），取该角色第一个启用账号</summary>
        public static UserSession LoginAs(UserRole role, string? display = null)
        {
            var acc = UserStore.List().FirstOrDefault(u => u.Role == role && u.Enabled);
            Current = new UserSession
            {
                UserId = acc?.Id ?? 0,
                UserName = acc?.UserName ?? role.ToString().ToLowerInvariant(),
                DisplayName = display ?? acc?.DisplayName ?? role.ToString(),
                Role = role
            };
            Log.Info("登录", $"{Current.DisplayName} 以免密方式进入系统（{role}）");
            return Current;
        }

        /// <summary>刷新当前会话（管理员修改了自己的姓名/角色后调用）</summary>
        public static void RefreshCurrent()
        {
            var acc = UserStore.Find(Current.UserName);
            if (acc == null) return;
            Current = new UserSession
            {
                UserId = acc.Id,
                UserName = acc.UserName,
                DisplayName = acc.DisplayName,
                Role = acc.Role,
                LoginTime = Current.LoginTime
            };
        }
    }
}
