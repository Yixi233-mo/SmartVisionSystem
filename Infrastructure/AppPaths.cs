namespace SmartVisionSystem.Infrastructure
{
    /// <summary>
    /// 全局路径管理：所有运行期数据（配方、日志、数据库、快照）统一落在程序目录下的 data / logs 中，
    /// 便于现场工程师直接拷贝工程、备份与排障。
    /// </summary>
    public static class AppPaths
    {
        public static string Root { get; }
        public static string DataDir { get; }
        public static string RecipeDir { get; }
        public static string SnapshotDir { get; }
        public static string LogDir { get; }
        public static string DbFile { get; }
        /// <summary>用户账号库（配置类数据，与生产记录分离，便于单独备份/下发）</summary>
        public static string UserDb { get; }

        static AppPaths()
        {
            Root = AppDomain.CurrentDomain.BaseDirectory;
            DataDir = Path.Combine(Root, "data");
            RecipeDir = Path.Combine(DataDir, "recipes");
            SnapshotDir = Path.Combine(DataDir, "snapshots");
            LogDir = Path.Combine(Root, "logs");
            DbFile = Path.Combine(DataDir, "production.db");
            UserDb = Path.Combine(DataDir, "users.db");

            foreach (var dir in new[] { DataDir, RecipeDir, SnapshotDir, LogDir })
            {
                try { Directory.CreateDirectory(dir); } catch { /* 目录创建失败不阻塞启动 */ }
            }
        }

        /// <summary>相对路径解析为绝对路径（配置文件里存相对路径，方便整体搬迁）</summary>
        public static string Resolve(string path)
            => string.IsNullOrWhiteSpace(path) ? path : Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
    }
}
