using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SmartVisionSystem.Infrastructure
{
    /// <summary>一条用户账号记录（不含口令，口令只以 PBKDF2 派生值形式落库）</summary>
    public sealed class UserAccount
    {
        public int Id { get; set; }
        public string UserName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public UserRole Role { get; set; } = UserRole.Operator;
        public bool Enabled { get; set; } = true;
        public string Remark { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? LastLoginAt { get; set; }
        public int LoginCount { get; set; }

        public string RoleText => Role switch
        {
            UserRole.Admin => "管理员",
            UserRole.Engineer => "设备工程师",
            _ => "操作员"
        };

        public string StatusText => Enabled ? "启用" : "停用";
        public string LastLoginText => LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "从未登录";
        /// <summary>表格里用的短格式，避免列宽不足被截成 "2026-10-28 1..."</summary>
        public string ShortLastLogin => LastLoginAt?.ToString("yy-MM-dd HH:mm") ?? "从未登录";

        public UserAccount Clone() => (UserAccount)MemberwiseClone();
    }

    /// <summary>
    /// 用户账号仓库（SQLite 持久化，data/users.db）。
    ///
    /// 安全设计：
    /// · 口令使用 PBKDF2-SHA256 + 每用户 16 字节随机盐 + 12 万次迭代派生，库文件泄漏也无法反推明文；
    /// · 校验用固定时间比较，规避计时侧信道；
    /// · 账号与生产数据分库，便于单独备份/下发，也避免生产库被清理时误删账号。
    /// </summary>
    public static class UserStore
    {
        private const int Pbkdf2Iterations = 120_000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;

        private static readonly object _sync = new();
        private static bool _seeded;

        private static string ConnStr => new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.UserDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        private static SqliteConnection Open()
        {
            var conn = new SqliteConnection(ConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
            return conn;
        }

        // ─────────────────────────── 初始化 ───────────────────────────

        public static void EnsureSeeded()
        {
            lock (_sync)
            {
                if (_seeded) return;

                using (var conn = Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS Users (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            UserName TEXT NOT NULL UNIQUE COLLATE NOCASE,
                            DisplayName TEXT NOT NULL,
                            Role INTEGER NOT NULL,
                            PasswordSalt TEXT NOT NULL,
                            PasswordHash TEXT NOT NULL,
                            Enabled INTEGER NOT NULL DEFAULT 1,
                            Remark TEXT DEFAULT '',
                            CreatedAt TEXT NOT NULL,
                            LastLoginAt TEXT,
                            LoginCount INTEGER NOT NULL DEFAULT 0
                        );";
                    cmd.ExecuteNonQuery();
                }

                if (List().Count == 0) SeedDefaults();
                _seeded = true;
            }
        }

        /// <summary>首次运行写入内置账号（现场部署后请由管理员立即修改口令）</summary>
        private static void SeedDefaults()
        {
            Add(new UserAccount { UserName = "admin", DisplayName = "管理员", Role = UserRole.Admin, Remark = "内置管理员账号" }, "admin123");
            Add(new UserAccount { UserName = "engineer", DisplayName = "设备工程师", Role = UserRole.Engineer, Remark = "内置工程师账号" }, "eng123");
            Add(new UserAccount { UserName = "operator", DisplayName = "操作员", Role = UserRole.Operator, Remark = "产线快捷登录，建议设置口令" }, "");
            Log.Info("用户", "已初始化内置账号：admin / engineer / operator");
        }

        // ─────────────────────────── 查询 ───────────────────────────

        public static List<UserAccount> List()
        {
            EnsureSeededQuiet();
            var list = new List<UserAccount>();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT Id, UserName, DisplayName, Role, Enabled, Remark, CreatedAt, LastLoginAt, LoginCount
                                    FROM Users ORDER BY Role DESC, UserName;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new UserAccount
                    {
                        Id = r.GetInt32(0),
                        UserName = r.GetString(1),
                        DisplayName = r.GetString(2),
                        Role = (UserRole)r.GetInt32(3),
                        Enabled = r.GetInt32(4) != 0,
                        Remark = r.IsDBNull(5) ? "" : r.GetString(5),
                        CreatedAt = DateTime.TryParse(r.GetString(6), out var c) ? c : DateTime.Now,
                        LastLoginAt = r.IsDBNull(7) ? null : DateTime.TryParse(r.GetString(7), out var l) ? l : null,
                        LoginCount = r.GetInt32(8)
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("用户", "读取用户列表失败", ex);
            }
            return list;
        }

        /// <summary>内部用：避免 EnsureSeeded 递归</summary>
        private static void EnsureSeededQuiet()
        {
            if (_seeded) return;
            lock (_sync)
            {
                if (_seeded) return;
                using (var conn = Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Users (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        UserName TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        DisplayName TEXT NOT NULL,
                        Role INTEGER NOT NULL,
                        PasswordSalt TEXT NOT NULL,
                        PasswordHash TEXT NOT NULL,
                        Enabled INTEGER NOT NULL DEFAULT 1,
                        Remark TEXT DEFAULT '',
                        CreatedAt TEXT NOT NULL,
                        LastLoginAt TEXT,
                        LoginCount INTEGER NOT NULL DEFAULT 0);";
                    cmd.ExecuteNonQuery();
                }
                _seeded = true;
            }
        }

        public static UserAccount? Find(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            return List().FirstOrDefault(u => string.Equals(u.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static int CountEnabledAdmins()
            => List().Count(u => u.Role == UserRole.Admin && u.Enabled);

        /// <summary>登录校验（口令比对 + 启用状态）</summary>
        public static bool Verify(string userName, string password, out UserAccount? account, out string error)
        {
            account = null;
            error = "";
            var user = Find(userName);
            if (user == null) { error = "账号不存在"; return false; }
            if (!user.Enabled) { error = "账号已停用，请联系管理员"; return false; }

            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT PasswordSalt, PasswordHash FROM Users WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", user.Id);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) { error = "账号不存在"; return false; }

                var salt = Convert.FromBase64String(r.GetString(0));
                var expected = Convert.FromBase64String(r.GetString(1));
                var actual = Derive(password, salt);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                {
                    error = "口令错误";
                    return false;
                }
                account = user;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("用户", $"登录校验异常（{userName}）", ex);
                error = "账号校验异常，详见日志";
                return false;
            }
        }

        public static void RecordLogin(string userName)
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Users SET LastLoginAt = $t, LoginCount = LoginCount + 1 WHERE UserName = $u;";
                cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$u", userName);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log.Error("用户", "记录登录时间失败", ex);
            }
        }

        // ─────────────────────────── 增删改 ───────────────────────────

        public static (bool Ok, string Error) Add(UserAccount account, string password)
        {
            EnsureSeededQuiet();
            var v = Validate(account);
            if (!v.Ok) return v;
            if (Find(account.UserName) != null) return (false, $"账号 {account.UserName} 已存在");

            try
            {
                var salt = RandomNumberGenerator.GetBytes(SaltBytes);
                var hash = Derive(password, salt);

                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO Users (UserName, DisplayName, Role, PasswordSalt, PasswordHash, Enabled, Remark, CreatedAt, LoginCount)
                                    VALUES ($u,$d,$r,$s,$h,$e,$m,$c,0);";
                cmd.Parameters.AddWithValue("$u", account.UserName.Trim());
                cmd.Parameters.AddWithValue("$d", account.DisplayName.Trim());
                cmd.Parameters.AddWithValue("$r", (int)account.Role);
                cmd.Parameters.AddWithValue("$s", Convert.ToBase64String(salt));
                cmd.Parameters.AddWithValue("$h", Convert.ToBase64String(hash));
                cmd.Parameters.AddWithValue("$e", account.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$m", account.Remark ?? "");
                cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();

                Log.Info("用户", $"新增账号 {account.UserName}（{account.RoleText}）");
                return (true, "");
            }
            catch (Exception ex)
            {
                Log.Error("用户", $"新增账号 {account.UserName} 失败", ex);
                return (false, "写入数据库失败：" + ex.Message);
            }
        }

        public static (bool Ok, string Error) Update(UserAccount account)
        {
            var v = Validate(account);
            if (!v.Ok) return v;

            var existing = Find(account.UserName);
            if (existing == null) return (false, "账号不存在");

            // 防呆：不允许把最后一个启用的管理员降级或停用，否则系统将无人可管
            if (existing.Role == UserRole.Admin && existing.Enabled &&
                (!account.Enabled || account.Role != UserRole.Admin) &&
                CountEnabledAdmins() <= 1)
                return (false, "系统必须保留至少一个启用的管理员账号");

            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE Users SET DisplayName=$d, Role=$r, Enabled=$e, Remark=$m WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$d", account.DisplayName.Trim());
                cmd.Parameters.AddWithValue("$r", (int)account.Role);
                cmd.Parameters.AddWithValue("$e", account.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$m", account.Remark ?? "");
                cmd.Parameters.AddWithValue("$id", existing.Id);
                cmd.ExecuteNonQuery();

                Log.Info("用户", $"修改账号 {account.UserName}（{account.RoleText}/{(account.Enabled ? "启用" : "停用")}）");
                return (true, "");
            }
            catch (Exception ex)
            {
                Log.Error("用户", $"修改账号 {account.UserName} 失败", ex);
                return (false, "写入数据库失败：" + ex.Message);
            }
        }

        public static (bool Ok, string Error) ChangePassword(string userName, string newPassword)
        {
            var user = Find(userName);
            if (user == null) return (false, "账号不存在");

            try
            {
                var salt = RandomNumberGenerator.GetBytes(SaltBytes);
                var hash = Derive(newPassword, salt);

                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Users SET PasswordSalt=$s, PasswordHash=$h WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$s", Convert.ToBase64String(salt));
                cmd.Parameters.AddWithValue("$h", Convert.ToBase64String(hash));
                cmd.Parameters.AddWithValue("$id", user.Id);
                cmd.ExecuteNonQuery();

                Log.Info("用户", $"重置账号 {userName} 的口令");
                return (true, "");
            }
            catch (Exception ex)
            {
                Log.Error("用户", $"重置 {userName} 口令失败", ex);
                return (false, "写入数据库失败：" + ex.Message);
            }
        }

        public static (bool Ok, string Error) Delete(string userName)
        {
            var user = Find(userName);
            if (user == null) return (false, "账号不存在");
            if (string.Equals(userName, Auth.Current.UserName, StringComparison.OrdinalIgnoreCase))
                return (false, "不能删除当前登录的账号");
            if (user.Role == UserRole.Admin && user.Enabled && CountEnabledAdmins() <= 1)
                return (false, "系统必须保留至少一个启用的管理员账号");

            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Users WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$id", user.Id);
                cmd.ExecuteNonQuery();

                Log.Alarm("用户", $"删除账号 {userName}（操作人：{Auth.Current.DisplayName}）");
                return (true, "");
            }
            catch (Exception ex)
            {
                Log.Error("用户", $"删除账号 {userName} 失败", ex);
                return (false, "删除失败：" + ex.Message);
            }
        }

        // ─────────────────────────── 内部 ───────────────────────────

        private static (bool Ok, string Error) Validate(UserAccount a)
        {
            var name = a.UserName?.Trim() ?? "";
            if (name.Length < 3 || name.Length > 20) return (false, "账号长度需为 3~20 个字符");
            foreach (var c in name)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '-')
                    return (false, "账号只允许字母、数字、下划线、点、短横线");

            if (string.IsNullOrWhiteSpace(a.DisplayName)) return (false, "姓名不能为空");
            if (a.DisplayName.Trim().Length > 20) return (false, "姓名过长");
            return (true, "");
        }

        private static byte[] Derive(string password, byte[] salt)
            => Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
    }
}
