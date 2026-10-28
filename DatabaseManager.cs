using System.Collections.Concurrent;
using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartVisionSystem.Infrastructure;

namespace SmartVisionSystem
{
    /// <summary>一条检测记录（对应数据库一行），是质量追溯的最小单元</summary>
    public sealed class DetectRecord
    {
        public DateTime Time { get; init; } = DateTime.Now;
        public string SN { get; init; } = "";
        public string Recipe { get; init; } = "";
        /// <summary>OK / NG / MISS</summary>
        public string Result { get; init; } = "NG";
        public double OffsetX { get; init; }
        public double OffsetY { get; init; }
        public double OffsetXPx { get; init; }
        public double OffsetYPx { get; init; }
        public double Score { get; init; }
        public double Angle { get; init; }
        public double Area { get; init; }
        public double SurfaceRatio { get; init; }
        public double CtMs { get; init; }
        public double CycleMs { get; init; }
        public string NgReason { get; init; } = "";
    }

    /// <summary>历史数据查询条件</summary>
    public sealed class HistoryFilter
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? Result { get; set; }
        public string? Recipe { get; set; }
        public string? SnKeyword { get; set; }
        public int Limit { get; set; } = 1000;
    }

    /// <summary>
    /// 数据持久化：SQLite（WAL 模式）+ 单写线程异步入库。
    ///
    /// 为什么不让工位线程直接写库：磁盘 IO 抖动（尤其机械硬盘或杀软扫描）会让单条 INSERT 耗时飙到几十毫秒，
    /// 直接拖垮节拍。这里工位只做入队，落盘由后台线程批量事务提交，实测单条摊薄 &lt; 0.2ms。
    /// </summary>
    public sealed class DatabaseManager : IDisposable
    {
        private readonly string _connStr;
        private readonly BlockingCollection<DetectRecord> _queue = new(new ConcurrentQueue<DetectRecord>(), 50000);
        private readonly Thread _writer;
        private volatile bool _disposed;

        public long PendingCount => _queue.Count;
        public long WrittenCount { get; private set; }

        public DatabaseManager(string? dbFile = null)
        {
            var file = dbFile ?? AppPaths.DbFile;
            _connStr = new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString();

            EnsureSchema();

            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "SVS.DbWriter" };
            _writer.Start();
            Log.Info("数据库", $"生产数据库就绪：{file}");
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
            return conn;
        }

        private void EnsureSchema()
        {
            try
            {
                using var conn = Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS DetectLog (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Time TEXT NOT NULL,
                            SN TEXT,
                            Recipe TEXT,
                            Result TEXT,
                            OffsetX REAL, OffsetY REAL,
                            OffsetXPx REAL, OffsetYPx REAL,
                            Score REAL, Angle REAL,
                            Area REAL, SurfaceRatio REAL,
                            CtMs REAL, CycleMs REAL,
                            NgReason TEXT
                        );
                        CREATE INDEX IF NOT EXISTS IX_DetectLog_Time ON DetectLog(Time);
                        CREATE INDEX IF NOT EXISTS IX_DetectLog_Result ON DetectLog(Result);
                        CREATE INDEX IF NOT EXISTS IX_DetectLog_SN ON DetectLog(SN);";
                    cmd.ExecuteNonQuery();
                }

                // 老版本数据库平滑升级：缺失列自动补齐
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA table_info(DetectLog);";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) existing.Add(reader.GetString(1));
                }

                var wanted = new (string Name, string Type)[]
                {
                    ("Recipe", "TEXT"), ("OffsetXPx", "REAL"), ("OffsetYPx", "REAL"),
                    ("Angle", "REAL"), ("Area", "REAL"), ("SurfaceRatio", "REAL"),
                    ("CtMs", "REAL"), ("CycleMs", "REAL"), ("NgReason", "TEXT")
                };
                foreach (var (name, type) in wanted)
                {
                    if (existing.Contains(name)) continue;
                    using var alter = conn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE DetectLog ADD COLUMN {name} {type};";
                    alter.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                Log.Error("数据库", "初始化数据库结构失败", ex);
                throw;
            }
        }

        /// <summary>入队（非阻塞，工位线程调用）</summary>
        public void Insert(DetectRecord record)
        {
            if (_disposed) return;
            if (!_queue.TryAdd(record))
                Log.Warn("数据库", "写入队列已满，本条检测记录丢弃");
        }

        private void WriterLoop()
        {
            var batch = new List<DetectRecord>(64);
            try
            {
                foreach (var first in _queue.GetConsumingEnumerable())
                {
                    batch.Clear();
                    batch.Add(first);
                    while (batch.Count < 64 && _queue.TryTake(out var next)) batch.Add(next);

                    try { WriteBatch(batch); }
                    catch (Exception ex) { Log.Error("数据库", "批量写入失败", ex); }
                }
            }
            catch (Exception ex)
            {
                Log.Error("数据库", "写库线程异常退出", ex);
            }
        }

        private void WriteBatch(List<DetectRecord> batch)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO DetectLog
                (Time, SN, Recipe, Result, OffsetX, OffsetY, OffsetXPx, OffsetYPx, Score, Angle, Area, SurfaceRatio, CtMs, CycleMs, NgReason)
                VALUES ($t,$sn,$rc,$res,$ox,$oy,$oxp,$oyp,$s,$a,$ar,$sr,$ct,$cy,$ng);";

            var pT = cmd.Parameters.Add("$t", SqliteType.Text);
            var pSn = cmd.Parameters.Add("$sn", SqliteType.Text);
            var pRc = cmd.Parameters.Add("$rc", SqliteType.Text);
            var pRes = cmd.Parameters.Add("$res", SqliteType.Text);
            var pOx = cmd.Parameters.Add("$ox", SqliteType.Real);
            var pOy = cmd.Parameters.Add("$oy", SqliteType.Real);
            var pOxp = cmd.Parameters.Add("$oxp", SqliteType.Real);
            var pOyp = cmd.Parameters.Add("$oyp", SqliteType.Real);
            var pS = cmd.Parameters.Add("$s", SqliteType.Real);
            var pA = cmd.Parameters.Add("$a", SqliteType.Real);
            var pAr = cmd.Parameters.Add("$ar", SqliteType.Real);
            var pSr = cmd.Parameters.Add("$sr", SqliteType.Real);
            var pCt = cmd.Parameters.Add("$ct", SqliteType.Real);
            var pCy = cmd.Parameters.Add("$cy", SqliteType.Real);
            var pNg = cmd.Parameters.Add("$ng", SqliteType.Text);

            foreach (var r in batch)
            {
                pT.Value = r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
                pSn.Value = r.SN;
                pRc.Value = r.Recipe;
                pRes.Value = r.Result;
                pOx.Value = r.OffsetX;
                pOy.Value = r.OffsetY;
                pOxp.Value = r.OffsetXPx;
                pOyp.Value = r.OffsetYPx;
                pS.Value = r.Score;
                pA.Value = r.Angle;
                pAr.Value = r.Area;
                pSr.Value = r.SurfaceRatio;
                pCt.Value = r.CtMs;
                pCy.Value = r.CycleMs;
                pNg.Value = r.NgReason;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            WrittenCount += batch.Count;
        }

        // ─────────────────────────── 查询 ───────────────────────────

        private (string Sql, List<SqliteParameter> Params) BuildWhere(HistoryFilter f)
        {
            var sb = new StringBuilder(" WHERE 1=1");
            var ps = new List<SqliteParameter>();
            if (f.From.HasValue) { sb.Append(" AND Time >= $from"); ps.Add(new SqliteParameter("$from", f.From.Value.ToString("yyyy-MM-dd HH:mm:ss"))); }
            if (f.To.HasValue) { sb.Append(" AND Time <= $to"); ps.Add(new SqliteParameter("$to", f.To.Value.ToString("yyyy-MM-dd HH:mm:ss"))); }
            if (!string.IsNullOrWhiteSpace(f.Result) && f.Result != "全部") { sb.Append(" AND Result = $res"); ps.Add(new SqliteParameter("$res", f.Result)); }
            if (!string.IsNullOrWhiteSpace(f.Recipe) && f.Recipe != "全部") { sb.Append(" AND Recipe = $rc"); ps.Add(new SqliteParameter("$rc", f.Recipe)); }
            if (!string.IsNullOrWhiteSpace(f.SnKeyword)) { sb.Append(" AND SN LIKE $sn"); ps.Add(new SqliteParameter("$sn", $"%{f.SnKeyword.Trim()}%")); }
            return (sb.ToString(), ps);
        }

        /// <summary>按条件查询（返回中文列名，直接绑定 DataGridView）</summary>
        public DataTable Query(HistoryFilter f)
        {
            var (where, ps) = BuildWhere(f);
            // 列别名刻意保持简短：表格列宽只有 90px 左右，长中文列名在界面上会被截断成"偏差"两个字
            var sql = $@"SELECT Id AS 序号, Time AS 时间, SN AS 流水号, Recipe AS 配方, Result AS 结果,
                                ROUND(OffsetX,4) AS X偏差mm, ROUND(OffsetY,4) AS Y偏差mm,
                                ROUND(OffsetXPx,2) AS X偏差px, ROUND(OffsetYPx,2) AS Y偏差px,
                                ROUND(Score,4) AS 得分, ROUND(Angle,2) AS 角度,
                                ROUND(Area,0) AS 面积px², ROUND(SurfaceRatio*100,3) AS 表面pct,
                                ROUND(CtMs,2) AS CTms, ROUND(CycleMs,1) AS 节拍ms,
                                NgReason AS 不良原因
                         FROM DetectLog{where} ORDER BY Id DESC LIMIT {Math.Clamp(f.Limit, 1, 20000)};";

            var dt = new DataTable();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var p in ps) cmd.Parameters.Add(p);
                using var reader = cmd.ExecuteReader();
                dt.Load(reader);
            }
            catch (Exception ex)
            {
                Log.Error("数据库", "查询历史数据失败", ex);
            }
            return dt;
        }

        /// <summary>条件汇总（用于看板/追溯统计）</summary>
        public (long Total, long Ok, long Ng) Summary(HistoryFilter f)
        {
            var (where, ps) = BuildWhere(f);
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*), SUM(CASE WHEN Result='OK' THEN 1 ELSE 0 END), SUM(CASE WHEN Result<>'OK' THEN 1 ELSE 0 END) FROM DetectLog{where};";
                foreach (var p in ps) cmd.Parameters.Add(p);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    long total = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                    long ok = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    long ng = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    return (total, ok, ng);
                }
            }
            catch (Exception ex)
            {
                Log.Error("数据库", "统计查询失败", ex);
            }
            return (0, 0, 0);
        }

        public long TotalRows()
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM DetectLog;";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch { return 0; }
        }

        /// <summary>导出 CSV（UTF-8 BOM，Excel 直接打开不乱码）</summary>
        public int ExportCsv(string path, HistoryFilter f)
        {
            var dt = Query(f);
            try
            {
                using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
                var headers = dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
                sw.WriteLine(string.Join(",", headers.Select(Escape)));
                int n = 0;
                foreach (DataRow row in dt.Rows)
                {
                    sw.WriteLine(string.Join(",", row.ItemArray.Select(v => Escape(v?.ToString() ?? ""))));
                    n++;
                }
                Log.Info("数据库", $"导出 CSV 成功：{path}（{n} 条）");
                return n;

                static string Escape(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
            }
            catch (Exception ex)
            {
                Log.Error("数据库", $"导出 CSV 失败：{path}", ex);
                return 0;
            }
        }

        /// <summary>清空历史（管理员权限动作，需二次确认）</summary>
        public bool ClearAll()
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM DetectLog; VACUUM;";
                cmd.ExecuteNonQuery();
                Log.Alarm("数据库", "历史检测数据已被管理员清空");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("数据库", "清空历史失败", ex);
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _writer?.Join(2000); } catch { }
        }
    }
}
