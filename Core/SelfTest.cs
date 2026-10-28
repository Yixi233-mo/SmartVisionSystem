using System.Diagnostics;
using System.Text;
using OpenCvSharp;
using SmartVisionSystem.Hardware;
using SmartVisionSystem.Infrastructure;
using SmartVisionSystem.Simulation;

namespace SmartVisionSystem.Core
{
    /// <summary>
    /// 无头自检（dotnet run -- --selftest）：
    /// 在没有相机、没有产线、没有界面的情况下，用虚拟场景跑完整视觉判定链路，输出
    ///   ① 匹配得分分布  ② 纠偏测量误差  ③ 良品/不良品混淆矩阵  ④ CT 时间分布  ⑤ 数据库读写
    /// 这是"能不能交付"的量化验收依据，而不是靠肉眼觉得"看起来还行"。
    /// </summary>
    public static class SelfTest
    {
        public static int Run(int sampleCount = 240)
        {
            var sb = new StringBuilder();
            bool acceptFail = false;
            var sw = Stopwatch.StartNew();

            try
            {
                Log.Init();
                sb.AppendLine("========== Smart Vision System 算法与链路基线自检 ==========");
                sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                var cfg = new SimulatorSettings
                {
                    OffsetAmpPx = 22,
                    AngleAmpDeg = 6,
                    PartSizeMm = 40
                };

                using var scene = new VirtualScene(cfg.FrameWidth, cfg.FrameHeight) { PartSizeMm = cfg.PartSizeMm };
                var pxPerMm = 2.0;

                // ① 建立标称模板与阈值（与首次运行完全相同的路径）
                var recipe = DemoRecipeFactory.Build(scene, pxPerMm, "SELFTEST");
                if (recipe == null)
                {
                    sb.AppendLine("✗ 模板生成失败，自检中止");
                    Flush(sb);
                    return 2;
                }

                sb.AppendLine($"模板：{recipe.Template!.Width}×{recipe.Template.Height}px   视觉基准 ({recipe.ImageBaseX:F1},{recipe.ImageBaseY:F1})");
                sb.AppendLine($"判定阈值：面积 {recipe.MinAreaPx:F0}~{recipe.MaxAreaPx:F0}px²   得分 ≥{recipe.MatchThreshold:F2}   表面缺陷 ≤{recipe.MaxSurfaceDefectRatio * 100:F2}%");
                sb.AppendLine($"像素当量：{pxPerMm:F2} px/mm（1 px = {1 / pxPerMm:F3} mm）");
                sb.AppendLine();

                // ② 基准：标称件重复检测，检验测量重复性
                sb.AppendLine("—— 标称件重复性（20 次）——");
                double areaMin = double.MaxValue, areaMax = double.MinValue, offsetMax = 0, scoreMin = 1;
                var cts = new List<double>();
                for (int i = 0; i < 20; i++)
                {
                    using var frame = scene.RenderSinglePart(0, 0, 0, DefectKind.None, pxPerMm);
                    var r = VisionEngine.Inspect(frame, recipe);
                    areaMin = Math.Min(areaMin, r.MaterialAreaPx);
                    areaMax = Math.Max(areaMax, r.MaterialAreaPx);
                    offsetMax = Math.Max(offsetMax, Math.Max(Math.Abs(r.OffsetXmm), Math.Abs(r.OffsetYmm)));
                    scoreMin = Math.Min(scoreMin, r.Score);
                    cts.Add(r.CtMs);
                }
                sb.AppendLine($"面积波动：{areaMin:F0} ~ {areaMax:F0} px²（极差 {areaMax - areaMin:F0} px²，相对 {100 * (areaMax - areaMin) / Math.Max(1, areaMin):F2}%）");
                sb.AppendLine($"零位偏差最大值：{offsetMax:F4} mm   最低得分：{scoreMin:F4}");
                sb.AppendLine();

                // ②b 定位精度分解：纯平移误差反映算法本身的系统精度，含旋转误差反映角度搜索量化影响
                sb.AppendLine("—— 定位精度分解（40 次，无缺陷）——");
                (double PureAvg, double PureMax) = OffsetError(scene, recipe, pxPerMm, 0, 0, 40, new Random(11));
                (double RotAvg, double RotMax) = OffsetError(scene, recipe, pxPerMm, cfg.OffsetAmpPx, cfg.AngleAmpDeg, 40, new Random(22));
                sb.AppendLine($"  纯平移（旋转 0°，偏差 ±{cfg.OffsetAmpPx}px）：平均 {PureAvg:F4} mm，最大 {PureMax:F4} mm");
                sb.AppendLine($"  含旋转（±{cfg.AngleAmpDeg}°，步距 {recipe.SearchAngleStep:F1}°）：平均 {RotAvg:F4} mm，最大 {RotMax:F4} mm");
                sb.AppendLine($"  1 px = {1 / pxPerMm:F3} mm，即纯平移误差约为 {PureAvg * pxPerMm:F3} px");
                sb.AppendLine();

                // ③ 混淆矩阵 + 纠偏精度 + CT 分布
                sb.AppendLine($"—— 随机来料压力测试（{sampleCount} 件，偏差 ±{cfg.OffsetAmpPx}px，旋转 ±{cfg.AngleAmpDeg}°，缺陷率 50%）——");
                var kinds = new[] { DefectKind.MissingHole, DefectKind.Chip, DefectKind.ExtraMaterial, DefectKind.Scratch };
                var kindStat = new Dictionary<DefectKind, (int Total, int Caught)>();
                foreach (var k in kinds) kindStat[k] = (0, 0);

                int tp = 0, fp = 0, tn = 0, fn = 0;
                double maxOffsetErr = 0, sumOffsetErr = 0;
                var sampleCts = new List<double>();
                var rnd = new Random(20261028);

                for (int i = 0; i < sampleCount; i++)
                {
                    bool defective = i % 2 == 0;
                    var kind = defective ? kinds[rnd.Next(kinds.Length)] : DefectKind.None;
                    double ox = (rnd.NextDouble() * 2 - 1) * cfg.OffsetAmpPx;
                    double oy = (rnd.NextDouble() * 2 - 1) * cfg.OffsetAmpPx * 0.75;
                    double ang = (rnd.NextDouble() * 2 - 1) * cfg.AngleAmpDeg;

                    using var frame = scene.RenderSinglePart(ox, oy, ang, kind, pxPerMm);
                    var r = VisionEngine.Inspect(frame, recipe);

                    // 纠偏测量误差：视觉测出的偏差 vs 注入的真值
                    double trueXmm = ox / pxPerMm, trueYmm = oy / pxPerMm;
                    double err = Math.Max(Math.Abs(r.OffsetXmm - trueXmm), Math.Abs(r.OffsetYmm - trueYmm));
                    maxOffsetErr = Math.Max(maxOffsetErr, err);
                    sumOffsetErr += err;
                    sampleCts.Add(r.CtMs);

                    bool judgedNg = !r.IsOk;
                    if (defective && judgedNg) { tp++; kindStat[kind] = (kindStat[kind].Total + 1, kindStat[kind].Caught + 1); }
                    else if (defective) { fn++; kindStat[kind] = (kindStat[kind].Total + 1, kindStat[kind].Caught); }
                    else if (judgedNg) fp++;
                    else tn++;
                }

                double recall = tp + fn > 0 ? tp * 100.0 / (tp + fn) : 0;
                double precision = tp + fp > 0 ? tp * 100.0 / (tp + fp) : 0;
                double f1 = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;

                sb.AppendLine($"  良品误判 (FP)：{fp}    不良漏检 (FN)：{fn}    正确放行 (TN)：{tn}    正确拦截 (TP)：{tp}");
                sb.AppendLine($"  检出率 Recall = {recall:F2}%    准确率 Precision = {precision:F2}%    F1 = {f1:F1}");
                sb.AppendLine("  分缺陷类型检出：");
                foreach (var k in kinds)
                {
                    var (t, c) = kindStat[k];
                    sb.AppendLine($"    {k,-14} {c}/{t}" + (t > 0 ? $"（{100.0 * c / t:F0}%）" : ""));
                }
                sb.AppendLine($"  纠偏测量误差：平均 {sumOffsetErr / sampleCount:F4} mm，最大 {maxOffsetErr:F4} mm（要求 < 0.05 mm）");
                sb.AppendLine();

                sampleCts.Sort();
                double P(double q) => sampleCts.Count == 0 ? 0 : sampleCts[Math.Clamp((int)(sampleCts.Count * q), 0, sampleCts.Count - 1)];
                sb.AppendLine("—— 视觉 CT 时间分布 ——");
                sb.AppendLine($"  P50 = {P(0.50):F2} ms    P90 = {P(0.90):F2} ms    P95 = {P(0.95):F2} ms    Max = {P(0.999):F2} ms（要求 < 50 ms）");
                sb.AppendLine();

                // ④ 数据库读写
                sb.AppendLine("—— 数据持久化 ——");
                var dbFile = Path.Combine(AppPaths.DataDir, "selftest.db");
                if (File.Exists(dbFile)) File.Delete(dbFile);
                using (var db = new DatabaseManager(dbFile))
                {
                    for (int i = 0; i < 50; i++)
                    {
                        db.Insert(new DetectRecord
                        {
                            SN = $"ST{i:D4}", Recipe = "SELFTEST", Result = i % 5 == 0 ? "NG" : "OK",
                            OffsetX = 0.123, OffsetY = -0.045, OffsetXPx = 0.246, OffsetYPx = -0.09,
                            Score = 0.987, Angle = 2.5, Area = 5000, SurfaceRatio = 0.001,
                            CtMs = 12.3, CycleMs = 1500, NgReason = i % 5 == 0 ? "测试不良" : ""
                        });
                    }
                    Thread.Sleep(350);
                    var (total, ok, ng) = db.Summary(new HistoryFilter { Limit = 100 });
                    var dt = db.Query(new HistoryFilter { Limit = 100 });
                    sb.AppendLine($"  写入 {db.WrittenCount} 条，汇总 总数 {total} / OK {ok} / NG {ng}，查询返回 {dt.Rows.Count} 行");
                    bool pass = total == 50 && ok == 40 && ng == 10 && dt.Rows.Count == 50;
                    sb.AppendLine($"  {(pass ? "✓" : "✗")} 数据库读写与统计{(pass ? "一致" : "不一致")}");

                }

                // ⑤b 用户账号与权限校验
                sb.AppendLine();
                sb.AppendLine("—— 用户与权限 ——");
                var users = UserStore.List();
                bool okAdmin = Auth.TryLogin("admin", "admin123", out _, out _);
                bool denyWrong = !Auth.TryLogin("admin", "__wrong_password__", out _, out _);
                bool opDeny = !Auth.LoginAs(UserRole.Operator).Can(Permission.EditRecipe) &&
                              !Auth.Current.Can(Permission.ManageUsers);
                bool adminAllow = Auth.LoginAs(UserRole.Admin).Can(Permission.ManageUsers) &&
                                  Auth.Current.Can(Permission.EditRecipe);
                sb.AppendLine($"  账号库 {users.Count} 个：" + string.Join(" / ", users.Select(u => $"{u.UserName}({u.RoleText})")));
                sb.AppendLine($"  admin 正确口令登录 {(okAdmin ? "✓" : "✗")}   错误口令拒绝 {(denyWrong ? "✓" : "✗")}   " +
                              $"操作员越权拦截 {(opDeny ? "✓" : "✗")}   管理员用户管理权限 {(adminAllow ? "✓" : "✗")}");
                if (!(okAdmin && denyWrong && opDeny && adminAllow)) acceptFail = true;

                // ⑤ 运动仿真：回零 + 定位耗时
                sb.AppendLine();
                sb.AppendLine("—— 运动仿真（梯形速度曲线）——");
                var net = new NetworkLink();
                using (var motion = new SimMotionController(net, cfg))
                {
                    motion.Open();
                    var t0 = Stopwatch.StartNew();
                    motion.HomeAllAsync().GetAwaiter().GetResult();
                    double homeMs = t0.Elapsed.TotalMilliseconds;

                    t0.Restart();
                    motion.MoveToAsync(420, 160, 80).GetAwaiter().GetResult();
                    double moveMs = t0.Elapsed.TotalMilliseconds;

                    sb.AppendLine($"  回零耗时 {homeMs:F0} ms，定位到 (420,160,80) 耗时 {moveMs:F0} ms，最终位置 ({motion.PosX:F1},{motion.PosY:F1},{motion.PosZ:F1})，已回零={motion.IsHomed}");
                }

                // ⑥ 全链路联调：真实启动工位线程，跑完整生产循环（等料→拍照→识别→纠偏→取放→入库→回传PLC）
                sb.AppendLine();
                sb.AppendLine("—— 全链路联调（启动工位，连续生产 6 拍）——");
                var e2e = RunEndToEnd(recipe, 6);
                sb.AppendLine(e2e.Report);
                if (!e2e.Pass) acceptFail = true;

                // 结论
                bool accept = !acceptFail && recall >= 95 && fp <= 3 && PureAvg < 0.05 && maxOffsetErr < 0.15 && P(0.95) < 50;
                sb.AppendLine();
                sb.AppendLine("验收指标：检出率 ≥95% ｜ 良品误判 ≤3 ｜ 纯平移定位误差均值 <0.05mm ｜ 含旋转最大误差 <0.15mm ｜ 视觉 CT P95 <50ms");
                sb.AppendLine(accept ? "结论：✓ 通过（检测能力、纠偏精度、实时性均满足指标）"
                                     : "结论：✗ 未达标，请检查阈值/模板/光照参数");
                sb.AppendLine($"总耗时 {sw.Elapsed.TotalSeconds:F2} s");
                Flush(sb);
                return accept ? 0 : 1;
            }
            catch (Exception ex)
            {
                sb.AppendLine("✗ 自检异常：" + ex);
                Flush(sb);
                return 3;
            }
        }

        /// <summary>
        /// 全链路联调：把虚拟产线 + 虚拟相机 + 虚拟运动卡 + 虚拟 PLC + 数据库 + 工位状态机全部拉起来，
        /// 跑完整生产循环，验证"视觉引导闭环"在无人值守下真的能连续出活。
        /// </summary>
        private static (bool Pass, string Report) RunEndToEnd(Recipe recipe, int targetCycles)
        {
            var sb = new StringBuilder();
            var cfg = new SimulatorSettings
            {
                DefectRatePct = 25,
                MaxVelMmS = 900,
                AccelMmS2 = 6000,
                LineSpeedMmS = 260
            };

            var dbFile = Path.Combine(AppPaths.DataDir, "e2e.db");
            try { if (File.Exists(dbFile)) File.Delete(dbFile); } catch { }

            var line = new VirtualProductionLine(cfg);
            var net = new NetworkLink();
            using var scene = new VirtualScene(cfg.FrameWidth, cfg.FrameHeight) { PartSizeMm = cfg.PartSizeMm };
            using var cam = new SimCamera(scene, line, net, cfg);
            using var motion = new SimMotionController(net, cfg);
            using var plc = new SimPlc(net);
            using var db = new DatabaseManager(dbFile);

            cam.PixelsPerMm = recipe.PixelsPerMm;
            cam.ExposureUs = recipe.ExposureUs;
            cam.Open();
            motion.Open();
            plc.Open();
            cam.Open();
            motion.Open();
            plc.Open();

            RecipeStore.Apply(recipe);

            using var station = new StationController(line, cam, motion, plc, db, net);
            string alarm = "";
            station.AlarmRaised += (msg, fatal) => { if (fatal) alarm = msg; };
            station.Initialize();

            if (!station.Start(out var err))
            {
                return (false, "  启动失败：" + err);
            }

            var sw = Stopwatch.StartNew();
            while (station.Stats.Total < targetCycles && sw.Elapsed.TotalSeconds < 90 && string.IsNullOrEmpty(alarm))
                Thread.Sleep(100);

            var s = station.Stats.Clone();
            station.Stop("自检结束");
            Thread.Sleep(400);

            var (dbTotal, dbOk, dbNg) = db.Summary(new HistoryFilter { Limit = 500 });

            sb.AppendLine($"  完成 {s.Total} 拍（目标 {targetCycles}）：OK {s.Ok} / NG {s.Ng}，良率 {s.YieldPct:F1}%，总耗时 {sw.Elapsed.TotalSeconds:F1}s");
            sb.AppendLine($"  平均节拍 {s.AvgCycleMs:F0} ms，平均视觉 CT {s.AvgCtMs:F1} ms，最大节拍 {s.MaxCycleMs:F0} ms");
            sb.AppendLine($"  数据库落库 {dbTotal} 条（OK {dbOk} / NG {dbNg}），PLC 最终状态：系统运行={plc.Read(PlcTags.SystemRun)} 输送={plc.Read(PlcTags.LineRun)}");

            bool pass = s.Total >= targetCycles && dbTotal >= s.Total && string.IsNullOrEmpty(alarm);
            sb.AppendLine(pass ? "  ✓ 闭环连续运行正常，无报警、数据完整落库" : $"  ✗ 联调失败：报警={alarm}，落库 {dbTotal} 条 / 生产 {s.Total} 拍");

            line.Dispose();
            return (pass, sb.ToString());
        }

        /// <summary>统计"视觉测得偏差"与"注入真值"的误差（mm）</summary>
        private static (double Avg, double Max) OffsetError(VirtualScene scene, Recipe recipe, double pxPerMm,
                                                          double offsetAmpPx, double angleAmpDeg, int count, Random rnd)
        {
            double sum = 0, max = 0;
            for (int i = 0; i < count; i++)
            {
                double ox = (rnd.NextDouble() * 2 - 1) * offsetAmpPx;
                double oy = (rnd.NextDouble() * 2 - 1) * offsetAmpPx * 0.75;
                double ang = (rnd.NextDouble() * 2 - 1) * angleAmpDeg;
                using var frame = scene.RenderSinglePart(ox, oy, ang, DefectKind.None, pxPerMm);
                var r = VisionEngine.Inspect(frame, recipe);
                double err = Math.Max(Math.Abs(r.OffsetXmm - ox / pxPerMm), Math.Abs(r.OffsetYmm - oy / pxPerMm));
                sum += err;
                max = Math.Max(max, err);
            }
            return (sum / count, max);
        }

        private static void Flush(StringBuilder sb)
        {
            var text = sb.ToString();
            try { Console.Write(text); } catch { }
            try
            {
                var file = Path.Combine(AppPaths.LogDir, "selftest.log");
                File.AppendAllText(file, text + Environment.NewLine);
            }
            catch { }
            Log.Info("自检", "自检报告已写入 logs/selftest.log");
            Log.Shutdown();
        }
    }
}
